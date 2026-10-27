using LANCommander.Plugins.GOG.Storage;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.GOG.Uploads;

/// <summary>A file an administrator has uploaded.</summary>
public sealed record GogUploadedFile(string Name, long Size);

/// <summary>Raised when an upload is refused, with a message fit to show the administrator.</summary>
public sealed class GogUploadException(string message) : Exception(message);

/// <summary>
/// Holds installers administrators have uploaded until they are imported.
/// </summary>
/// <remarks>
/// <para>
/// Each upload is a set of files - an installer executable and the <c>-N.bin</c> slices that go
/// with it - kept together in its own directory under the work directory, since the installer
/// reader finds the slices beside the executable.
/// </para>
/// <para>
/// A file is written under a temporary name and only takes its own once it has arrived whole, so
/// an upload cut short never passes for a finished one.
/// </para>
/// </remarks>
public sealed class GogUploadStore(GogSettingsStore settings, ILogger<GogUploadStore> logger)
{
    private const string PartialSuffix = ".part";

    private static readonly string[] Extensions = [".exe", ".bin"];

    /// <summary>Where every upload is kept.</summary>
    public string Root => Path.Combine(settings.WorkDirectory, "Uploads");

    public string DirectoryFor(Guid uploadId) => Path.Combine(Root, uploadId.ToString("N"));

    /// <summary>Stores one file of an upload, replacing any file of the same name.</summary>
    /// <exception cref="GogUploadException">The file is not part of a GOG installer.</exception>
    public async Task<GogUploadedFile> SaveAsync(Guid uploadId, string? fileName, Stream content, CancellationToken cancellationToken)
    {
        if (uploadId == Guid.Empty)
            throw new GogUploadException("The upload has no id.");

        // Browsers send a bare name, but nothing stops a client sending a path.
        var name = (fileName ?? string.Empty).Split('/', '\\').Last().Trim();

        if (name.Length == 0 || name.StartsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new GogUploadException($"\"{fileName}\" is not a usable file name.");

        if (!IsInstallerFile(name))
            throw new GogUploadException($"{name} is not part of a GOG installer. Upload the setup .exe and any .bin files that came with it.");

        var directory = DirectoryFor(uploadId);
        var path = Path.Combine(directory, name);
        var partial = path + PartialSuffix;

        Directory.CreateDirectory(directory);

        try
        {
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                await content.CopyToAsync(file, 1 << 20, cancellationToken);

            File.Move(partial, path, overwrite: true);
        }
        catch
        {
            TryDelete(partial);

            throw;
        }

        return new GogUploadedFile(name, new FileInfo(path).Length);
    }

    /// <summary>The files of an upload that have arrived whole.</summary>
    public IReadOnlyList<GogUploadedFile> Files(Guid uploadId)
    {
        var directory = DirectoryFor(uploadId);

        if (!Directory.Exists(directory))
            return [];

        return new DirectoryInfo(directory)
            .EnumerateFiles()
            .Where(file => IsInstallerFile(file.Name))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => new GogUploadedFile(file.Name, file.Length))
            .ToList();
    }

    /// <summary>The installer executable of an upload.</summary>
    /// <exception cref="GogUploadException">The upload holds no executable, or more than one.</exception>
    public string FindInstaller(Guid uploadId)
    {
        var executables = Files(uploadId)
            .Where(file => file.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return executables.Count switch
        {
            1 => Path.Combine(DirectoryFor(uploadId), executables[0].Name),
            0 => throw new GogUploadException("No installer has been uploaded, or it is no longer on the server. Upload it again."),
            _ => throw new GogUploadException(
                $"{executables.Count} installers were uploaded together ({string.Join(", ", executables.Select(file => file.Name))}). Upload one at a time."),
        };
    }

    /// <summary>Deletes an upload and everything in it.</summary>
    public void Discard(Guid uploadId)
    {
        try
        {
            var directory = DirectoryFor(uploadId);

            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not delete the upload {UploadId}: {Message}", uploadId, exception.Message);
        }
    }

    /// <summary>
    /// Deletes uploads nothing refers to any more: ones whose import has been forgotten, and ones
    /// never imported at all.
    /// </summary>
    /// <param name="keep">Uploads still wanted.</param>
    /// <param name="minimumAge">How long an upload is left alone, so one still being made is not taken.</param>
    public int Prune(IEnumerable<Guid> keep, TimeSpan minimumAge)
    {
        if (!Directory.Exists(Root))
            return 0;

        var wanted = keep.Select(id => id.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoff = DateTime.UtcNow - minimumAge;
        var pruned = 0;

        foreach (var directory in new DirectoryInfo(Root).EnumerateDirectories())
        {
            if (wanted.Contains(directory.Name) || !Guid.TryParseExact(directory.Name, "N", out var id))
                continue;

            var lastWrite = directory.EnumerateFiles().Select(file => file.LastWriteTimeUtc).DefaultIfEmpty(directory.LastWriteTimeUtc).Max();

            if (lastWrite > cutoff)
                continue;

            Discard(id);

            pruned++;
        }

        return pruned;
    }

    private static bool IsInstallerFile(string name)
        => Extensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
