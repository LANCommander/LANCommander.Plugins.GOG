using System.IO.Compression;
using System.IO.Pipelines;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.Xz.Filters;

namespace LANCommander.GOG.Inno.Streams;

/// <summary>
/// Turns a compressed payload chunk into a readable stream of its decompressed bytes.
/// </summary>
/// <remarks>
/// Every chunk begins with a four-byte marker, after which the bytes are a bare codec
/// stream: zlib needs no preamble, LZMA1 is introduced by five property bytes, and LZMA2 by
/// a single dictionary-size byte. Which codec applies is recorded once in the setup header,
/// not per chunk.
/// </remarks>
internal static class ChunkDecoder
{
    /// <summary>Marker that introduces every compressed payload chunk.</summary>
    private static ReadOnlySpan<byte> ChunkMagic => [0x7A, 0x6C, 0x62, 0x1A];

    /// <summary>Bytes of LZMA1 properties that introduce an LZMA1 stream.</summary>
    private const int Lzma1PropertyLength = 5;

    /// <summary>
    /// Wraps <paramref name="source"/>, which must be positioned at a chunk marker, in a
    /// stream over the chunk's decompressed contents.
    /// </summary>
    /// <param name="source">The payload, positioned at the start of the chunk.</param>
    /// <param name="compression">The codec the installer declared for its payload.</param>
    /// <param name="compressedLength">Compressed length of the chunk, used to bound the codec.</param>
    public static Stream Open(Stream source, PayloadCompression compression, long compressedLength)
    {
        Span<byte> magic = stackalloc byte[ChunkMagic.Length];

        if (source.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) < magic.Length)
            throw new CorruptInstallerException("Payload ends before a chunk that the header refers to.");

        if (!magic.SequenceEqual(ChunkMagic))
            throw new CorruptInstallerException(
                "Expected a compressed payload chunk but found something else. " +
                "The installer or one of its .bin slices is damaged.");

        return compression switch
        {
            PayloadCompression.Stored => source,
            PayloadCompression.ZLib => new ZLibStream(source, CompressionMode.Decompress),
            PayloadCompression.Lzma1 => OpenLzma1(source, compressedLength),
            PayloadCompression.Lzma2 => OpenLzma2(source),

            // Inno can produce bzip2 payloads, but no GOG installer has been seen using one
            // and no bzip2 decoder is reachable here, so say so plainly rather than guess.
            PayloadCompression.BZip2 => throw new UnsupportedInstallerException(
                "The installer's payload is bzip2 compressed, which this library does not support."),

            _ => throw new UnsupportedInstallerException(
                "The installer's payload uses a compression method this library does not support."),
        };
    }

    /// <summary>
    /// Wraps an LZMA2 stream, whose only preamble is a single dictionary-size byte.
    /// </summary>
    private static Stream OpenLzma2(Stream source)
    {
        var properties = new byte[1];

        if (source.ReadAtLeast(properties, properties.Length, throwOnEndOfStream: false) < properties.Length)
            throw new CorruptInstallerException("Payload chunk ends before its LZMA2 properties.");

        var filter = new Lzma2Filter();

        filter.Init(properties);
        filter.SetBaseStream(source);

        return filter;
    }

    /// <summary>
    /// Wraps a raw LZMA1 stream.
    /// </summary>
    /// <remarks>
    /// The available LZMA1 decoder pushes its output rather than being pulled from, so it is
    /// run on its own task and piped back to the caller. The pipe applies backpressure, so a
    /// multi-gigabyte chunk never has to be buffered.
    /// </remarks>
    private static Stream OpenLzma1(Stream source, long compressedLength)
    {
        var properties = new byte[Lzma1PropertyLength];

        if (source.ReadAtLeast(properties, properties.Length, throwOnEndOfStream: false) < properties.Length)
            throw new CorruptInstallerException("Payload chunk ends before its LZMA properties.");

        var decoder = new Decoder();

        decoder.SetDecoderProperties(properties);

        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 4 * 1024 * 1024,
            resumeWriterThreshold: 1024 * 1024,
            useSynchronizationContext: false));

        _ = Task.Run(() =>
        {
            var writer = pipe.Writer.AsStream();

            try
            {
                decoder.Code(source, writer, compressedLength, outSize: -1, progress: null!);

                pipe.Writer.Complete();
            }
            catch (Exception exception)
            {
                pipe.Writer.Complete(exception);
            }
        });

        return pipe.Reader.AsStream();
    }
}
