using System.Buffers.Binary;
using LANCommander.GOG.Inno.Streams;

namespace LANCommander.GOG.Inno.Loader;

/// <summary>
/// The offset table embedded in the Inno Setup loader stub. It is the only map from the
/// installer executable to the setup header and the compressed payload.
/// </summary>
/// <param name="Version">Table layout revision; 1 for every Inno 5.x build observed.</param>
/// <param name="TotalSize">Declared total size of the installer executable.</param>
/// <param name="ExeOffset">Offset of the embedded uninstaller executable.</param>
/// <param name="ExeCompressedSize">Compressed size of the embedded uninstaller.</param>
/// <param name="ExeChecksum">CRC-32 of the embedded uninstaller.</param>
/// <param name="HeaderOffset">Offset of the compressed setup header block.</param>
/// <param name="PayloadOffset">
/// Offset of the embedded compressed payload, or 0 when the payload lives in external
/// <c>-N.bin</c> slices.
/// </param>
/// <param name="TableChecksum">CRC-32 of the preceding fields.</param>
public sealed record SetupLoaderOffsets(
    uint Version,
    uint TotalSize,
    uint ExeOffset,
    uint ExeCompressedSize,
    uint ExeChecksum,
    uint HeaderOffset,
    uint PayloadOffset,
    uint TableChecksum)
{
    /// <summary>
    /// Leading bytes of the offset-table signature.
    /// </summary>
    /// <remarks>
    /// Stock Inno Setup follows this with a fixed suffix, but GOG's <c>JR.Inno.Setup</c>
    /// fork substitutes its own bytes, so only the prefix may be matched. Candidates are
    /// accepted or rejected structurally instead, by <see cref="IsPlausible"/>.
    /// </remarks>
    public static ReadOnlySpan<byte> SignaturePrefix => "rDlPtS"u8;

    /// <summary>Length of the full signature field that precedes the numeric fields.</summary>
    public const int SignatureLength = 12;

    /// <summary>Total size of the table: a 12-byte signature followed by eight 32-bit fields.</summary>
    public const int TableLength = SignatureLength + 8 * sizeof(uint);

    /// <summary>The 12 signature bytes this table was found with, for diagnostics.</summary>
    public byte[] Signature { get; init; } = [];

    /// <summary>True when the payload is stored in external <c>-N.bin</c> slices.</summary>
    public bool UsesExternalSlices => PayloadOffset == 0;

    /// <summary>Reads a table from <paramref name="source"/>, which must start at the signature.</summary>
    public static SetupLoaderOffsets Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < TableLength)
            throw new CorruptInstallerException("Loader offset table is truncated.");

        var fields = source[SignatureLength..];

        Span<uint> values = stackalloc uint[8];

        for (var i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(fields[(i * 4)..]);

        return new SetupLoaderOffsets(
            Version: values[0],
            TotalSize: values[1],
            ExeOffset: values[2],
            ExeCompressedSize: values[3],
            ExeChecksum: values[4],
            HeaderOffset: values[5],
            PayloadOffset: values[6],
            TableChecksum: values[7])
        {
            Signature = source[..SignatureLength].ToArray(),
        };
    }

    /// <summary>
    /// Checks the table against the physical file, rejecting coincidental matches of the
    /// signature prefix inside unrelated data.
    /// </summary>
    /// <param name="fileLength">Length of the installer executable on disk.</param>
    public bool IsPlausible(long fileLength)
    {
        if (Version is 0 or > 2)
            return false;

        // The header must sit inside the file and leave room for its identifier.
        if (HeaderOffset == 0 || HeaderOffset + Versioning.InnoVersion.IdentifierLength > fileLength)
            return false;

        if (PayloadOffset > fileLength || ExeOffset > fileLength)
            return false;

        // TotalSize is written before any trailing signature is appended, so it is at or
        // just below the real length rather than exactly equal to it.
        return TotalSize > 0 && TotalSize <= fileLength;
    }

    /// <summary>
    /// True when <see cref="TableChecksum"/> matches the preceding fields. GOG's fork does
    /// not always maintain this, so a mismatch is informational rather than fatal.
    /// </summary>
    public bool ChecksumMatches(ReadOnlySpan<byte> table)
        => table.Length >= TableLength
           && Crc32.Compute(table[..(TableLength - sizeof(uint))]) == TableChecksum;
}
