using System.IO.Compression;
using System.Security.Cryptography;
using LANCommander.GOG.Gog;
using LANCommander.GOG.Manifest;
using Microsoft.Extensions.Logging;

namespace LANCommander.GOG.Packaging;

/// <summary>What producing the finished game tree wrote, and how much of it was checked.</summary>
/// <param name="FilesWritten">Number of files written.</param>
/// <param name="BytesWritten">Total bytes written.</param>
/// <param name="ContentAddressed">
/// True when the tree was assembled from content-addressed blobs rather than extracted directly.
/// </param>
/// <param name="Verified">
/// True when every file written was checked against a SHA-1 covering that whole file.
/// </param>
/// <param name="SkippedCount">
/// How many entries the installer carries that were not written. Each one says why at debug level.
/// </param>
/// <param name="VerificationNote">
/// What the verification does and does not cover, phrased for the run summary.
/// </param>
public sealed record GameTreeResult(
    int FilesWritten,
    long BytesWritten,
    bool ContentAddressed,
    bool Verified,
    int SkippedCount,
    string VerificationNote);

/// <summary>How far producing the game tree has got.</summary>
/// <param name="FilesCompleted">Files written so far.</param>
/// <param name="FilesTotal">Files that will be written in all.</param>
/// <param name="BytesCompleted">Bytes of payload read so far.</param>
/// <param name="BytesTotal">Bytes of payload that will be read in all.</param>
public readonly record struct GameTreeProgress(int FilesCompleted, int FilesTotal, long BytesCompleted, long BytesTotal)
{
    /// <summary>Progress as a value between 0 and 1.</summary>
    public double Fraction => BytesTotal == 0 ? 0 : Math.Clamp((double)BytesCompleted / BytesTotal, 0, 1);
}

/// <summary>
/// Produces the finished game tree from an installer, so that the archive holds the game as it
/// would be after installation rather than as the installer stores it.
/// </summary>
public static class GameTreeExtractor
{
    /// <summary>
    /// Writes the installed layout of <paramref name="installer"/> into <paramref name="workDirectory"/>.
    /// </summary>
    public static async Task<GameTreeResult> ExtractAsync(
        GogInstaller installer,
        string workDirectory,
        bool verify,
        ILogger logger,
        CancellationToken cancellationToken = default)
        => await ExtractAsync(installer, workDirectory, verify, logger, progress: null, cancellationToken);

    /// <summary>
    /// Writes the installed layout of <paramref name="installer"/> into <paramref name="workDirectory"/>,
    /// reporting how far it has got.
    /// </summary>
    public static async Task<GameTreeResult> ExtractAsync(
        GogInstaller installer,
        string workDirectory,
        bool verify,
        ILogger logger,
        IProgress<GameTreeProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workDirectory);

        return installer.Manifest.UsesContentAddressedPayload
            ? await AssembleStagedAsync(installer, workDirectory, verify, logger, progress, cancellationToken)
            : await ExtractDirectAsync(installer, workDirectory, verify, logger, progress, cancellationToken);
    }

    /// <summary>
    /// Extracts an ordinary installer, whose files are stored at the paths they install to.
    /// </summary>
    private static async Task<GameTreeResult> ExtractDirectAsync(
        GogInstaller installer,
        string workDirectory,
        bool verify,
        ILogger logger,
        IProgress<GameTreeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var relay = progress is null
            ? null
            : new SynchronousProgress<ExtractionProgress>(update => progress.Report(new GameTreeProgress(
                update.FilesCompleted, update.FilesTotal, update.BytesCompleted, update.BytesTotal)));

        var result = await installer.ExtractAllAsync(
            workDirectory,
            new ExtractionOptions
            {
                ApplicationFilesOnly = true,
                VerifyChecksums = verify,
                Overwrite = true,
                PreserveTimestamps = true,
            },
            relay,
            cancellationToken);

        // Nothing is dropped silently: every entry that did not make it says why.
        foreach (var skipped in result.Skipped)
            logger.LogDebug("Skipped {Path}: {Reason}", skipped.Entry.DestinationPath, skipped.Reason);

        return new GameTreeResult(
            result.FilesWritten,
            result.BytesWritten,
            ContentAddressed: false,
            Verified: verify,
            SkippedCount: result.Skipped.Count,
            VerificationNote: verify
                ? "Every extracted file was checked against the SHA-1 the installer records for it."
                : "Verification was disabled, so nothing written was checked.");
    }

    /// <summary>
    /// Assembles the tree of a content-addressed installer, whose payload holds hash-named blobs
    /// that the installer's own script would turn back into the real files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="GogInstaller.ExtractAllAsync"/> is no use here: the blobs live under the
    /// installer's scratch directory, so <see cref="ExtractionOptions.ApplicationFilesOnly"/>
    /// would skip every one of them, and extracting first and renaming afterwards writes the
    /// whole payload twice.
    /// </para>
    /// <para>
    /// The blobs are normally deflated on top of the payload's own compression - GOG's script
    /// calls <c>unzlib_file</c> on each one before merging - so they are inflated here as they are
    /// concatenated. Whether that applies is decided by trying it, because an installer that
    /// staged its files verbatim would need the raw bytes instead.
    /// </para>
    /// </remarks>
    private static async Task<GameTreeResult> AssembleStagedAsync(
        GogInstaller installer,
        string workDirectory,
        bool verify,
        ILogger logger,
        IProgress<GameTreeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workDirectory);
        var files = 0;
        var bytes = 0L;

        // Dependencies are redistributables the installer staged for its own use, not game files.
        var staged = installer.Manifest.StagedFiles.Where(file => !file.IsDependency).ToList();
        var dependencies = installer.Manifest.StagedFiles.Count - staged.Count;

        var bytesTotal = staged.Sum(file => file.FileIndices.Sum(index => installer.Files[index].Size));
        var bytesRead = 0L;

        var inflate = staged.Count > 0 && await BlobsAreDeflatedAsync(installer, staged[0], cancellationToken);

        logger.LogInformation(
            inflate
                ? "Assembling {Count} file(s) from deflated content-addressed blobs."
                : "Assembling {Count} file(s) from content-addressed blobs stored verbatim.",
            staged.Count);

        foreach (var file in staged)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = ResolveStagedPath(root, file.DestinationPath);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            try
            {
                await using var output = File.Create(destination);

                foreach (var index in file.FileIndices)
                {
                    var entry = installer.Files[index];

                    await using var input = installer.OpenRead(entry);

                    bytes += await CopyAsync(input, output, verify ? entry : null, inflate, cancellationToken);
                    bytesRead += entry.Size;
                }
            }
            catch (InvalidDataException exception)
            {
                throw new CorruptInstallerException(
                    $"\"{file.DestinationPath}\" is staged as deflated data that does not decompress.",
                    exception);
            }

            files++;

            progress?.Report(new GameTreeProgress(files, staged.Count, bytesRead, bytesTotal));

            logger.LogDebug(
                "Assembled {Path} from {Blobs} blob(s)", file.DestinationPath, file.FileIndices.Count);
        }

        return new GameTreeResult(
            files,
            bytes,
            ContentAddressed: true,
            Verified: false,
            SkippedCount: dependencies,
            VerificationNote: verify
                ? "The installer's SHA-1s cover the staged blobs rather than the files they assemble into, " +
                  "so each blob was checked but no assembled file was."
                : "Verification was disabled, so nothing written was checked. The installer records no " +
                  "checksum for an assembled file in any case.");
    }

    /// <summary>
    /// Decides whether the staged blobs carry deflated data, by inflating the start of one.
    /// </summary>
    /// <remarks>
    /// Reading the two-byte zlib header alone is not enough: roughly one byte pair in a thousand
    /// passes that check by chance, which over a library of games is a corrupt package waiting to
    /// happen. Producing a byte out of the other end settles it.
    /// </remarks>
    private static async Task<bool> BlobsAreDeflatedAsync(
        GogInstaller installer,
        StagedFile file,
        CancellationToken cancellationToken)
    {
        await using var input = installer.OpenRead(installer.Files[file.FileIndices[0]]);

        var buffer = new byte[4096];
        var read = await input.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);

        if (read < 2)
            return false;

        try
        {
            await using var zlib = new ZLibStream(
                new MemoryStream(buffer, 0, read), CompressionMode.Decompress);

            return await zlib.ReadAsync(new byte[1], cancellationToken) == 1;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies one blob, inflating it when asked and checking the stored bytes against the SHA-1
    /// recorded for them when <paramref name="verified"/> is given.
    /// </summary>
    /// <returns>How many bytes reached <paramref name="output"/>.</returns>
    private static async Task<long> CopyAsync(
        Stream source,
        Stream output,
        InstallerFileEntry? verified,
        bool inflate,
        CancellationToken cancellationToken)
    {
        var expected = verified?.Location?.Sha1 ?? [];

        using var hash = expected.Length > 0 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;

        // The installer's SHA-1 covers the blob as stored, so it is taken on the way in.
        await using var hashing = new HashingReadStream(source, hash);

        long written;

        if (inflate)
        {
            await using (var zlib = new ZLibStream(hashing, CompressionMode.Decompress, leaveOpen: true))
                written = await CopyAsync(zlib, output, cancellationToken);

            // The inflater stops at the end of the deflate stream, which can leave bytes of the
            // entry unread and so unhashed.
            await hashing.DrainAsync(cancellationToken);
        }
        else
        {
            written = await CopyAsync(hashing, output, cancellationToken);
        }

        if (hash is not null && !hash.GetCurrentHash().AsSpan().SequenceEqual(expected))
            throw new CorruptInstallerException(
                $"\"{verified!.DestinationPath}\" does not match the SHA-1 recorded for it.");

        return written;
    }

    private static async Task<long> CopyAsync(Stream source, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 16];
        var total = 0L;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);

            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            total += read;
        }

        return total;
    }

    /// <summary>
    /// Resolves a staged file's destination beneath the work directory, refusing anything that
    /// escapes it.
    /// </summary>
    /// <remarks>
    /// The path comes out of the installer, and this route bypasses the equivalent check inside
    /// <see cref="GogInstaller.ExtractAllAsync"/>.
    /// </remarks>
    private static string ResolveStagedPath(string root, string destinationPath)
    {
        var segments = InnoPath.ToLocalPath(destinationPath)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment is not ("." or ".."))
            .ToArray();

        if (segments.Length == 0)
            throw new CorruptInstallerException($"\"{destinationPath}\" has no usable destination path.");

        var path = Path.GetFullPath(Path.Combine([root, .. segments]));

        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new CorruptInstallerException(
                $"\"{destinationPath}\" resolves outside the extraction directory.");

        return path;
    }
}

/// <summary>
/// Reports on the calling thread rather than posting to a captured context, so that progress
/// arrives in order and before the work that reported it moves on.
/// </summary>
internal sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>
/// A read-only pass-through that hashes everything drawn through it.
/// </summary>
/// <remarks>
/// This exists so that a blob can be checked against the SHA-1 of its stored bytes while being
/// inflated in the same pass. Hashing the inflated output instead would check the wrong thing.
/// </remarks>
internal sealed class HashingReadStream(Stream inner, IncrementalHash? hash) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Draws whatever is left through the hash, so it covers the whole entry.</summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 16];

        while (await ReadAsync(buffer, cancellationToken) > 0)
        {
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);

        if (read > 0)
            hash?.AppendData(buffer[..read]);

        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);

        if (read > 0)
            hash?.AppendData(buffer.Span[..read]);

        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
