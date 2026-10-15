namespace LANCommander.GOG.Inno.Streams;

/// <summary>
/// Compression methods Inno Setup uses for the setup header block.
/// </summary>
internal enum HeaderCompression : byte
{
    /// <summary>No compression; the block is stored verbatim.</summary>
    Stored = 0,

    /// <summary>Raw LZMA1, introduced by five property bytes.</summary>
    Lzma1 = 1,

    /// <summary>zlib-wrapped deflate.</summary>
    ZLib = 2,
}

/// <summary>
/// Compression methods a payload chunk can use. Which one applies is recorded in the
/// setup header rather than in the chunk itself.
/// </summary>
public enum PayloadCompression
{
    /// <summary>No compression.</summary>
    Stored,

    /// <summary>zlib-wrapped deflate.</summary>
    ZLib,

    /// <summary>bzip2.</summary>
    BZip2,

    /// <summary>Raw LZMA1.</summary>
    Lzma1,

    /// <summary>Raw LZMA2.</summary>
    Lzma2,

    /// <summary>The installer declared a method this library does not implement.</summary>
    Unknown,
}
