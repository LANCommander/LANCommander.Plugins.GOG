using LANCommander.GOG;
using LANCommander.GOG.Gog;
using LANCommander.GOG.Packaging;
using LANCommander.Plugins.GOG.Api;
using LANCommander.Plugins.GOG.Storage;
using LANCommander.SDK.Enums;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.GOG.Jobs;

/// <summary>Runs one import from start to finish.</summary>
public interface IGogImportExecutor
{
    Task<GogImportOutcome> ExecuteAsync(GogImportJobSnapshot job, IGogImportReporter reporter, CancellationToken cancellationToken);
}

/// <summary>
/// Downloads a game's Windows installer from GOG, turns it into a package and imports it.
/// </summary>
/// <remarks>
/// Each import works in its own directory under the work directory. It needs room for the
/// installer, the extracted game and the package at once; the installer and the extracted game
/// are deleted before the import starts, since the server copies the archive again on its way in.
/// A failed import keeps its downloaded files, so retrying it resumes the download instead of
/// starting over.
/// </remarks>
public sealed class GogImportExecutor(
    GogApiClient api,
    GogDownloader downloader,
    GogSettingsStore settings,
    GogImportMapStore imports,
    IPackageImporter importer,
    IRedistributableProvider redistributables,
    ILogger<GogImportExecutor> logger) : IGogImportExecutor
{
    public const string Author = "LANCommander.Plugins.GOG";

    public async Task<GogImportOutcome> ExecuteAsync(
        GogImportJobSnapshot job,
        IGogImportReporter reporter,
        CancellationToken cancellationToken)
    {
        var options = settings.Current;
        var work = Path.Combine(settings.WorkDirectory, job.Id.ToString("N"));
        var downloads = Path.Combine(work, "download");
        var tree = Path.Combine(work, "game");
        var package = Path.Combine(work, "package.lcx");

        var succeeded = false;

        try
        {
            // Resolve
            var details = await api.GetProductDownloadsAsync(job.AccountId, job.ProductId, cancellationToken);
            var installer = SelectInstaller(details.Downloads?.Installers ?? [], job.Language)
                            ?? throw new GogImportException(NoInstallerMessage(details));

            reporter.Installer(installer.Name ?? job.Title, GogVersion.Clean(installer.Version));

            if (!string.Equals(installer.Language, job.Language, StringComparison.OrdinalIgnoreCase))
                reporter.Warning(
                    $"No {job.Language} installer is offered; imported the {installer.LanguageFull ?? installer.Language} one instead.");

            // Download
            reporter.Stage(GogImportStage.Downloading, "Downloading installer");

            var installerPath = await DownloadAsync(job, installer, downloads, options.VerifyChecksums, reporter, cancellationToken);

            // Extract and package
            reporter.Stage(GogImportStage.Extracting, "Extracting game files");

            DeleteDirectory(tree);

            var previous = imports.Get(job.ProductId);
            var links = await RedistributablesAsync(options.Redistributables, reporter, cancellationToken);
            var stage = GogPackageStage.Extracting;

            var result = await GogPackageBuilder.BuildAsync(
                installerPath,
                tree,
                package,
                new GogPackageOptions
                {
                    VerifyChecksums = options.VerifyChecksums,
                    GameId = previous?.GameId,
                    FallbackTitle = job.Title,
                    FallbackProductId = job.ProductId,
                    Author = Author,
                    Redistributables = links,
                },
                logger,
                new SynchronousProgress<GogPackageProgress>(update =>
                {
                    if (update.Stage != stage)
                    {
                        stage = update.Stage;
                        reporter.Stage(GogImportStage.Packaging, "Building package");
                    }

                    reporter.Progress(
                        update.Progress.BytesCompleted,
                        update.Progress.BytesTotal,
                        $"{update.Progress.FilesCompleted:N0} of {update.Progress.FilesTotal:N0} files");
                }),
                cancellationToken);

            foreach (var warning in result.Warnings)
                reporter.Warning(warning);

            // The installer and the tree are in the package now; free the space before the server
            // copies the archive out of it.
            if (!options.KeepWorkFiles)
            {
                DeleteDirectory(tree);
                DeleteDirectory(downloads);
            }

            // Import
            reporter.Stage(GogImportStage.Importing, "Importing into LANCommander");

            var gameId = (await importer.ImportFileAsync(package, job.StorageLocationId, ManifestType.Game, cancellationToken)).RecordId;

            imports.Record(new GogImportRecord
            {
                ProductId = job.ProductId,
                GameId = gameId,
                Title = result.Game.Title,
                Version = result.Game.Version,
                ImportedOn = DateTimeOffset.UtcNow,
            });

            succeeded = true;

            return new GogImportOutcome(gameId, result.Game.Version, result.Warnings);
        }
        catch (GogInstallerException exception)
        {
            // Every rejection the installer reader makes - an Inno 6 or RAR-wrapped build, a bzip2
            // or encrypted payload, a missing slice - already explains itself.
            throw new GogImportException($"The installer could not be read: {exception.Message}", exception);
        }
        finally
        {
            CleanUp(work, downloads, keepDownloads: !succeeded && !cancellationToken.IsCancellationRequested, options.KeepWorkFiles);
        }
    }

    /// <summary>
    /// Picks the Windows installer to import: the requested language, else English, else whatever
    /// there is.
    /// </summary>
    public static GogInstallerDownload? SelectInstaller(IEnumerable<GogInstallerDownload> installers, string language)
    {
        var windows = installers
            .Where(installer => string.Equals(installer.Os, "windows", StringComparison.OrdinalIgnoreCase))
            .Where(installer => installer.Files.Count > 0)
            .ToList();

        return windows.FirstOrDefault(installer => string.Equals(installer.Language, language, StringComparison.OrdinalIgnoreCase))
               ?? windows.FirstOrDefault(installer => string.Equals(installer.Language, "en", StringComparison.OrdinalIgnoreCase))
               ?? windows.FirstOrDefault();
    }

    private async Task<string> DownloadAsync(
        GogImportJobSnapshot job,
        GogInstallerDownload installer,
        string directory,
        bool verify,
        IGogImportReporter reporter,
        CancellationToken cancellationToken)
    {
        var total = installer.Files.Sum(file => file.Size);
        var completed = 0L;
        var paths = new List<string>();

        for (var index = 0; index < installer.Files.Count; index++)
        {
            var file = installer.Files[index];
            var before = completed;
            var message = installer.Files.Count > 1
                ? $"Downloading file {index + 1} of {installer.Files.Count}"
                : "Downloading installer";

            if (string.IsNullOrEmpty(file.Downlink))
                throw new GogImportException($"GOG lists a file of {installer.Name} with nowhere to download it from.");

            var path = await downloader.DownloadAsync(
                ct => api.ResolveDownlinkAsync(job.AccountId, file.Downlink, ct),
                directory,
                file.Size,
                verify,
                new SynchronousProgress<long>(bytes => reporter.Progress(before + bytes, total, message)),
                cancellationToken);

            completed += file.Size > 0 ? file.Size : new FileInfo(path).Length;
            paths.Add(path);
        }

        return paths.FirstOrDefault(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
               ?? throw new GogImportException(
                   $"{installer.Name ?? job.Title} downloaded no installer executable. GOG may be offering this game in a form the plugin cannot read.");
    }

    /// <summary>
    /// Looks up the redistributables the settings ask for, dropping any removed from the server
    /// since they were chosen.
    /// </summary>
    private async Task<IReadOnlyList<GogRedistributableLink>> RedistributablesAsync(
        IReadOnlyList<Guid> ids,
        IGogImportReporter reporter,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return [];

        var available = await redistributables.GetAsync(cancellationToken);
        var links = available
            .Where(redistributable => ids.Contains(redistributable.Id))
            .Select(redistributable => new GogRedistributableLink(redistributable.Id, redistributable.Name))
            .ToList();

        if (links.Count < ids.Count)
            reporter.Warning(ids.Count - links.Count == 1
                ? "1 redistributable chosen in the settings is no longer on the server and was not linked."
                : $"{ids.Count - links.Count} redistributables chosen in the settings are no longer on the server and were not linked.");

        return links;
    }

    private static string NoInstallerMessage(GogProductDetails details)
    {
        var systems = (details.Downloads?.Installers ?? [])
            .Select(installer => installer.Os)
            .Where(os => !string.IsNullOrEmpty(os))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return systems.Count == 0
            ? "GOG offers no offline installer for this game."
            : $"GOG offers no Windows installer for this game, only {string.Join(" and ", systems)}.";
    }

    private void CleanUp(string work, string downloads, bool keepDownloads, bool keepEverything)
    {
        if (keepEverything)
            return;

        try
        {
            if (!keepDownloads)
            {
                DeleteDirectory(work);

                return;
            }

            if (!Directory.Exists(work))
                return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(work))
            {
                if (string.Equals(Path.GetFullPath(entry), Path.GetFullPath(downloads), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not clean up {Directory}: {Message}", work, exception.Message);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}

/// <summary>Reports on the calling thread, in order.</summary>
internal sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
