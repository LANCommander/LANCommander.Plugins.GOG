namespace LANCommander.GOG.Manifest;

/// <summary>What a file entry contributes to the installation.</summary>
public enum InstallerFileKind
{
    /// <summary>An ordinary installed file.</summary>
    File,

    /// <summary>The uninstaller executable.</summary>
    UninstallerExe,

    /// <summary>A helper executable the installer registers.</summary>
    RegSvrExe,
}

/// <summary>
/// Where a file's bytes live inside the compressed payload.
/// </summary>
/// <param name="Index">Index of this location entry within the installer.</param>
/// <param name="FirstSlice">0-based slice the chunk starts in; slice 0 is <c>-1.bin</c>.</param>
/// <param name="LastSlice">0-based slice the chunk ends in.</param>
/// <param name="ChunkOffset">
/// Offset of the containing chunk, as recorded in the file table. For external slices this
/// is measured from the start of the <c>.bin</c> file; for a payload embedded in the
/// executable it is measured from the start of the payload.
/// </param>
/// <param name="OffsetInChunk">Offset of this file's bytes within the decompressed chunk.</param>
/// <param name="OriginalSize">Size of the file once decompressed.</param>
/// <param name="ChunkCompressedSize">Compressed size of the containing chunk.</param>
/// <param name="Sha1">SHA-1 of the decompressed file, or empty when not recorded.</param>
/// <param name="Timestamp">Last-write time recorded for the file.</param>
/// <param name="IsCompressed">False when the chunk stores the file verbatim.</param>
/// <param name="IsFiltered">
/// True when Inno's executable transform was applied to the bytes and must be reversed.
/// </param>
/// <param name="IsChunkEncrypted">True when the chunk is password protected.</param>
public sealed record FileLocation(
    int Index,
    uint FirstSlice,
    uint LastSlice,
    long ChunkOffset,
    long OffsetInChunk,
    long OriginalSize,
    long ChunkCompressedSize,
    byte[] Sha1,
    DateTime Timestamp,
    bool IsCompressed,
    bool IsFiltered,
    bool IsChunkEncrypted)
{
    /// <summary>
    /// Identifies the chunk this file shares with other entries. Files in the same chunk
    /// must be decompressed in ascending <see cref="OffsetInChunk"/> order.
    /// </summary>
    public (uint Slice, long Offset) ChunkId => (FirstSlice, ChunkOffset);

    /// <summary>The recorded SHA-1 as lowercase hex, or an empty string when absent.</summary>
    public string Sha1Hex => Sha1.Length == 0 ? string.Empty : Convert.ToHexStringLower(Sha1);
}

/// <summary>
/// A file the installer would write, together with a pointer to its bytes in the payload.
/// </summary>
/// <param name="Index">Index of this entry within the installer.</param>
/// <param name="DestinationPath">
/// Destination path with Inno constants left unresolved, e.g. <c>{app}\game.exe</c>.
/// </param>
/// <param name="SourcePath">Source path, used only by external files.</param>
/// <param name="Location">
/// Where the bytes live, or <see langword="null"/> for entries that install no data.
/// </param>
/// <param name="Attributes">Win32 file attributes applied to the file.</param>
/// <param name="Kind">What the entry contributes to the installation.</param>
/// <param name="IsExternal">
/// True when the file is not stored in the installer and would be read from the source
/// medium at install time.
/// </param>
/// <param name="RawOptions">
/// The installer's own option bits for this entry, preserved but not interpreted. Inno's
/// meanings for these vary by format version and are not decoded here; prefer the named
/// properties on this type.
/// </param>
/// <param name="Conditions">Guards controlling whether the entry applies.</param>
public sealed record InstallerFileEntry(
    int Index,
    string DestinationPath,
    string SourcePath,
    FileLocation? Location,
    uint Attributes,
    InstallerFileKind Kind,
    bool IsExternal,
    uint RawOptions,
    EntryConditions Conditions)
{
    /// <summary>Size of the file once decompressed, or 0 when it carries no data.</summary>
    public long Size => Location?.OriginalSize ?? 0;

    /// <summary>SHA-1 of the file's contents as lowercase hex, or an empty string.</summary>
    public string Sha1Hex => Location?.Sha1Hex ?? string.Empty;

    /// <summary>Last-write time recorded for the file.</summary>
    public DateTime Timestamp => Location?.Timestamp ?? default;

    /// <summary>True when this entry's bytes can be read from the payload.</summary>
    public bool HasContent => Location is not null && !IsExternal;

    /// <summary>The leading Inno constant of the destination, such as <c>{app}</c>.</summary>
    public string DestinationRoot => InnoPath.GetConstant(DestinationPath);

    /// <summary>
    /// True when the file is installed into the application directory, as opposed to a
    /// scratch location used only while the installer runs.
    /// </summary>
    public bool IsApplicationFile
        => DestinationRoot.Equals("{app}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the file is unpacked to a temporary directory for the installer's own use
    /// and would not remain after installation.
    /// </summary>
    public bool IsTemporary
        => DestinationRoot.Equals("{tmp}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The destination path relative to the application directory, or <see langword="null"/>
    /// when the file installs elsewhere.
    /// </summary>
    public string? RelativeToApp
        => IsApplicationFile ? DestinationPath[DestinationRoot.Length..].TrimStart('\\') : null;

    /// <summary>Renders the destination path and size.</summary>
    public override string ToString() => $"{DestinationPath} ({Size} bytes)";
}
