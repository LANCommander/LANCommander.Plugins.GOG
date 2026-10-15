using System.Security.Cryptography;
using LANCommander.GOG.Manifest;

namespace LANCommander.GOG.Inno.Streams;

/// <summary>
/// Writes an installer's files to disk in the order that reads the payload most cheaply.
/// </summary>
/// <remarks>
/// Files sharing a chunk are decompressed from one pass over that chunk rather than
/// restarting it per file, which turns a whole-installer extract into a single linear read.
/// </remarks>
internal sealed class PayloadExtractor(
    PayloadReader payload,
    IReadOnlyList<InstallerFileEntry> files,
    ExtractionOptions options)
{
    public async Task<ExtractionResult> ExtractAsync(
        string destination,
        IProgress<ExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);

        var skipped = new List<SkippedEntry>();
        var selected = new List<InstallerFileEntry>();

        foreach (var entry in files)
        {
            var reason = Reject(entry);

            if (reason is null)
                selected.Add(entry);
            else if (reason.Length > 0)
                skipped.Add(new SkippedEntry(entry, reason));
        }

        var bytesTotal = selected.Sum(entry => entry.Size);
        var filesWritten = 0;
        var bytesWritten = 0L;

        // Group by chunk, then order within each chunk, so every chunk is decoded once.
        var chunks = selected
            .GroupBy(entry => entry.Location!.ChunkId)
            .OrderBy(group => group.Key.Slice)
            .ThenBy(group => group.Key.Offset);

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ordered = chunk.OrderBy(entry => entry.Location!.OffsetInChunk).ToList();

            using var stream = payload.OpenChunk(ordered[0].Location!);

            var chunkPosition = 0L;

            foreach (var entry in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var location = entry.Location!;

                // Duplicate entries can point back at a file already passed in this chunk;
                // those need their own pass rather than breaking the sequential read.
                if (location.OffsetInChunk < chunkPosition)
                {
                    using var standalone = payload.OpenRead(location);

                    bytesWritten += await WriteAsync(standalone, entry, destination, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    PayloadReader.SkipExactly(stream, location.OffsetInChunk - chunkPosition);

                    using var bounded = PayloadReader.Wrap(stream, location, leaveOpen: true);

                    bytesWritten += await WriteAsync(bounded, entry, destination, cancellationToken)
                        .ConfigureAwait(false);

                    chunkPosition = location.OffsetInChunk + location.OriginalSize;
                }

                filesWritten++;

                progress?.Report(new ExtractionProgress(
                    entry, filesWritten, selected.Count, bytesWritten, bytesTotal));
            }
        }

        return new ExtractionResult(filesWritten, bytesWritten, skipped);
    }

    /// <summary>
    /// Decides whether an entry is extracted. Returns <see langword="null"/> to extract it,
    /// an empty string to drop it silently, or a reason to record it as skipped.
    /// </summary>
    private string? Reject(InstallerFileEntry entry)
    {
        if (!entry.HasContent)
            return entry.IsExternal
                ? "stored outside the installer"
                : string.Empty;

        if (entry.Location!.IsChunkEncrypted)
            return "the payload chunk is password protected";

        // Reported rather than dropped quietly: some newer GOG builds stage the whole game
        // outside {app}, and a silent skip there looks indistinguishable from success.
        if (options.ApplicationFilesOnly && !entry.IsApplicationFile)
            return $"not an application file (installs to {entry.DestinationRoot})";

        // An explicit caller-supplied filter is a deliberate choice, so it stays quiet.
        if (options.Filter is not null && !options.Filter(entry))
            return string.Empty;

        return null;
    }

    private async Task<long> WriteAsync(
        Stream source,
        InstallerFileEntry entry,
        string destination,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(destination, entry);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path) && !options.Overwrite)
        {
            // Still drain the file's bytes so the shared chunk stays correctly positioned.
            PayloadReader.SkipExactly(source, entry.Size);

            return 0;
        }

        long written;

        using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            written = options.VerifyChecksums
                ? await CopyVerifiedAsync(source, output, entry, cancellationToken).ConfigureAwait(false)
                : await CopyAsync(source, output, cancellationToken).ConfigureAwait(false);
        }

        if (options.PreserveTimestamps && entry.Timestamp != default)
            File.SetLastWriteTimeUtc(path, entry.Timestamp);

        return written;
    }

    private static async Task<long> CopyAsync(Stream source, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 16];
        var total = 0L;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read <= 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            total += read;
        }

        return total;
    }

    /// <summary>
    /// Copies a file while hashing it, and fails if the result does not match the digest the
    /// installer recorded.
    /// </summary>
    private static async Task<long> CopyVerifiedAsync(
        Stream source,
        Stream output,
        InstallerFileEntry entry,
        CancellationToken cancellationToken)
    {
        var expected = entry.Location!.Sha1;

        if (expected.Length == 0)
            return await CopyAsync(source, output, cancellationToken).ConfigureAwait(false);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        var buffer = new byte[1 << 16];
        var total = 0L;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read <= 0)
                break;

            hash.AppendData(buffer, 0, read);

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            total += read;
        }

        var actual = hash.GetHashAndReset();

        if (!actual.AsSpan().SequenceEqual(expected))
            throw new CorruptInstallerException(
                $"\"{entry.DestinationPath}\" did not match its recorded checksum " +
                $"(expected {Convert.ToHexStringLower(expected)}, got {Convert.ToHexStringLower(actual)}).");

        return total;
    }

    /// <summary>
    /// Maps an entry's Inno destination path to a path under <paramref name="destination"/>,
    /// refusing anything that would escape it.
    /// </summary>
    private static string ResolvePath(string destination, InstallerFileEntry entry)
    {
        var relative = entry.RelativeToApp ?? StripConstant(entry.DestinationPath);
        var segments = relative
            .Replace('\\', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment is not ("." or ".."))
            .ToArray();

        if (segments.Length == 0)
            throw new CorruptInstallerException($"\"{entry.DestinationPath}\" has no usable destination path.");

        var root = Path.GetFullPath(destination);
        var path = Path.GetFullPath(Path.Combine([root, .. segments]));

        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new CorruptInstallerException(
                $"\"{entry.DestinationPath}\" resolves outside the extraction directory.");

        return path;
    }

    /// <summary>Removes a leading Inno constant such as <c>{tmp}</c> from a path.</summary>
    private static string StripConstant(string path)
    {
        if (!path.StartsWith('{'))
            return path;

        var end = path.IndexOf('}');

        return end < 0 ? path : path[(end + 1)..].TrimStart('\\');
    }
}
