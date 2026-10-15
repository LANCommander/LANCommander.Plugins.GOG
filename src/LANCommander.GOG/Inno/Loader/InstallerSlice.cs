namespace LANCommander.GOG.Inno.Loader;

/// <summary>
/// One physical file holding part of the compressed payload: either the installer
/// executable itself, or an external <c>-N.bin</c> file.
/// </summary>
/// <param name="Path">Absolute path to the file backing this slice.</param>
/// <param name="PayloadStart">Offset within that file where payload bytes begin.</param>
/// <param name="FileLength">Total length of the backing file.</param>
/// <param name="Number">0-based slice number, matching the numbering in the file table.</param>
/// <param name="IsExternal">True when this is a <c>-N.bin</c> file rather than the executable.</param>
public sealed record InstallerSlice(
    string Path,
    long PayloadStart,
    long FileLength,
    int Number,
    bool IsExternal)
{
    /// <summary>Magic that introduces every external payload slice.</summary>
    public static ReadOnlySpan<byte> SliceMagic => "idska32"u8;

    /// <summary>Bytes occupied by a slice's own header: the magic plus a 32-bit field.</summary>
    public const int SliceHeaderLength = 8 + sizeof(uint);

    /// <summary>
    /// The base that a chunk offset from the file table is measured against.
    /// </summary>
    /// <remarks>
    /// External slices record chunk offsets from the start of the <c>.bin</c> file, so they
    /// already include the slice header. A payload embedded in the executable records them
    /// from the start of the payload instead, so the payload's own offset must be added.
    /// </remarks>
    public long ChunkOrigin => IsExternal ? 0 : PayloadStart;

    /// <summary>File name of the backing file, for diagnostics.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);
}
