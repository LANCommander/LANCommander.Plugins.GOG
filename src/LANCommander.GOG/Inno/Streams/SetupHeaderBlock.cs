using System.Buffers.Binary;
using System.IO.Compression;
using LANCommander.GOG.Inno.Versioning;
using SharpCompress.Compressors.LZMA;

namespace LANCommander.GOG.Inno.Streams;

/// <summary>
/// The two compressed blocks that make up an installer's setup header.
/// </summary>
/// <param name="Version">Format version declared by the block identifier.</param>
/// <param name="Header">
/// Block one: the setup header, every entry array, and the wizard images.
/// </param>
/// <param name="FileLocations">
/// Block two: the file location table describing where each file sits in the payload.
/// </param>
internal sealed record SetupHeaderBlocks(InnoVersion Version, byte[] Header, byte[] FileLocations);

/// <summary>
/// Reads the compressed blocks that hold an installer's setup header.
/// </summary>
/// <remarks>
/// A block is a five-byte header guarded by its own CRC-32, followed by the compressed
/// bytes split into runs of up to 4 KiB that are each preceded by a CRC-32 of the run. The
/// block header's stored size counts those checksum bytes as well as the compressed data.
/// Only the first block is introduced by a 64-byte format identifier.
/// </remarks>
internal static class SetupHeaderBlock
{
    private const int ChunkSize = 4096;

    /// <summary>Bytes of LZMA properties that introduce an LZMA1 block.</summary>
    private const int LzmaPropertyLength = 5;

    /// <summary>Size of a block header: a CRC-32, a stored size, and a compression method.</summary>
    private const int BlockHeaderLength = sizeof(uint) + sizeof(uint) + 1;

    /// <summary>
    /// Reads and decompresses both setup header blocks starting at <paramref name="offset"/>.
    /// </summary>
    /// <param name="stream">The installer executable.</param>
    /// <param name="offset">Offset of the first block, from the loader offset table.</param>
    public static SetupHeaderBlocks Read(Stream stream, long offset)
    {
        stream.Position = offset;

        var identifier = new byte[InnoVersion.IdentifierLength];

        if (stream.ReadAtLeast(identifier, identifier.Length, throwOnEndOfStream: false) < identifier.Length)
            throw new CorruptInstallerException("Installer ends before its setup header identifier.");

        var version = InnoVersion.Parse(identifier);

        var header = ReadBlock(stream, "setup header");
        var fileLocations = ReadBlock(stream, "file location table");

        return new SetupHeaderBlocks(version, header, fileLocations);
    }

    /// <summary>
    /// Reads one block from the current stream position and returns its decompressed bytes.
    /// </summary>
    private static byte[] ReadBlock(Stream stream, string what)
    {
        Span<byte> blockHeader = stackalloc byte[BlockHeaderLength];

        if (stream.ReadAtLeast(blockHeader, blockHeader.Length, throwOnEndOfStream: false) < blockHeader.Length)
            throw new CorruptInstallerException($"Installer ends before the {what} block header.");

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader);
        var actual = Crc32.Compute(blockHeader[sizeof(uint)..]);

        if (expected != actual)
            throw new CorruptInstallerException(
                $"The {what} block header failed its checksum (expected 0x{expected:X8}, got 0x{actual:X8}). " +
                "The installer is truncated or damaged.");

        var storedSize = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader[sizeof(uint)..]);
        var compression = (HeaderCompression)blockHeader[^1];

        if (storedSize > int.MaxValue)
            throw new CorruptInstallerException($"The {what} claims an implausible size of {storedSize} bytes.");

        var stored = ReadStoredBytes(stream, (int)storedSize, what);

        return compression switch
        {
            HeaderCompression.Stored => stored,
            HeaderCompression.ZLib => Inflate(stored, what),
            HeaderCompression.Lzma1 => DecodeLzma1(stored, what),
            _ => throw new UnsupportedInstallerException(
                $"The {what} uses unknown compression method {(byte)compression}."),
        };
    }

    /// <summary>
    /// Reads <paramref name="storedSize"/> bytes of framed block data, verifying and
    /// stripping the CRC-32 that precedes each run.
    /// </summary>
    private static byte[] ReadStoredBytes(Stream stream, int storedSize, string what)
    {
        using var payload = new MemoryStream(storedSize);

        var buffer = new byte[ChunkSize];
        var remaining = storedSize;

        Span<byte> checksum = stackalloc byte[sizeof(uint)];

        while (remaining > 0)
        {
            if (remaining < sizeof(uint))
                throw new CorruptInstallerException($"The {what} ends mid-way through a block checksum.");

            if (stream.ReadAtLeast(checksum, checksum.Length, throwOnEndOfStream: false) < checksum.Length)
                throw new CorruptInstallerException($"Installer ends mid-way through the {what}.");

            remaining -= sizeof(uint);

            var runLength = Math.Min(ChunkSize, remaining);
            var run = buffer.AsSpan(0, runLength);

            if (stream.ReadAtLeast(run, runLength, throwOnEndOfStream: false) < runLength)
                throw new CorruptInstallerException($"Installer ends mid-way through the {what}.");

            remaining -= runLength;

            var expected = BinaryPrimitives.ReadUInt32LittleEndian(checksum);
            var actual = Crc32.Compute(run);

            if (expected != actual)
                throw new CorruptInstallerException(
                    $"A block of the {what} failed its checksum " +
                    $"(expected 0x{expected:X8}, got 0x{actual:X8}). The installer is truncated or damaged.");

            payload.Write(run);
        }

        return payload.ToArray();
    }

    private static byte[] Inflate(byte[] stored, string what)
    {
        try
        {
            using var source = new MemoryStream(stored, writable: false);
            using var inflater = new ZLibStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();

            inflater.CopyTo(output);

            return output.ToArray();
        }
        catch (Exception exception) when (exception is not GogInstallerException)
        {
            throw new CorruptInstallerException($"The {what} could not be decompressed.", exception);
        }
    }

    /// <summary>
    /// Decodes a raw LZMA1 stream introduced by five property bytes. Inno records no
    /// uncompressed size here, so the decoder runs until it meets the stream's end marker.
    /// </summary>
    private static byte[] DecodeLzma1(byte[] stored, string what)
    {
        if (stored.Length < LzmaPropertyLength)
            throw new CorruptInstallerException($"The {what} is too short to carry LZMA properties.");

        var decoder = new Decoder();

        decoder.SetDecoderProperties(stored[..LzmaPropertyLength]);

        using var source = new MemoryStream(stored, LzmaPropertyLength, stored.Length - LzmaPropertyLength, writable: false);
        using var output = new MemoryStream();

        try
        {
            decoder.Code(source, output, source.Length, outSize: -1, progress: null!);
        }
        catch (Exception exception) when (exception is not GogInstallerException)
        {
            throw new CorruptInstallerException($"The {what} could not be decompressed.", exception);
        }

        return output.ToArray();
    }
}
