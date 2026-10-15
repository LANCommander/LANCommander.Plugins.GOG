using LANCommander.GOG.Manifest;

namespace LANCommander.GOG;

/// <summary>
/// Controls how <see cref="GogInstaller.ExtractAllAsync"/> writes files to disk.
/// </summary>
public sealed record ExtractionOptions
{
    /// <summary>
    /// Verify each extracted file against the SHA-1 recorded in the installer, failing the
    /// extraction on a mismatch. On by default: a mismatch means silently corrupt output.
    /// </summary>
    public bool VerifyChecksums { get; init; } = true;

    /// <summary>
    /// Extract only files whose destination begins with <c>{app}</c>, which is the game
    /// itself rather than the installer's own temporary helpers. On by default.
    /// </summary>
    public bool ApplicationFilesOnly { get; init; } = true;

    /// <summary>
    /// Overwrite files that already exist at the destination. Off by default.
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// Apply the timestamp recorded in the installer to each extracted file. On by default.
    /// </summary>
    public bool PreserveTimestamps { get; init; } = true;

    /// <summary>
    /// An optional predicate selecting which entries to extract, applied after the other
    /// filters.
    /// </summary>
    public Func<InstallerFileEntry, bool>? Filter { get; init; }
}

/// <summary>
/// Reports progress while extracting.
/// </summary>
/// <param name="Entry">The file just written.</param>
/// <param name="FilesCompleted">How many files have been written so far.</param>
/// <param name="FilesTotal">How many files will be written in total.</param>
/// <param name="BytesCompleted">How many bytes have been written so far.</param>
/// <param name="BytesTotal">How many bytes will be written in total.</param>
public readonly record struct ExtractionProgress(
    InstallerFileEntry Entry,
    int FilesCompleted,
    int FilesTotal,
    long BytesCompleted,
    long BytesTotal)
{
    /// <summary>Fraction of the total bytes written so far, between 0 and 1.</summary>
    public double Fraction => BytesTotal == 0 ? 1 : (double)BytesCompleted / BytesTotal;
}

/// <summary>
/// What an extraction produced.
/// </summary>
/// <param name="FilesWritten">Number of files written.</param>
/// <param name="BytesWritten">Total bytes written.</param>
/// <param name="Skipped">Entries that were not written, with the reason.</param>
public sealed record ExtractionResult(
    int FilesWritten,
    long BytesWritten,
    IReadOnlyList<SkippedEntry> Skipped);

/// <summary>An entry that was not extracted.</summary>
/// <param name="Entry">The entry in question.</param>
/// <param name="Reason">Why it was not written.</param>
public sealed record SkippedEntry(InstallerFileEntry Entry, string Reason);
