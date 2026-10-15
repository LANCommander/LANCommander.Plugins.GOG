using LANCommander.GOG.Inno.Loader;
using LANCommander.GOG.Manifest;

namespace LANCommander.GOG.Inno.Streams;

/// <summary>
/// Decompresses files out of an installer's payload.
/// </summary>
/// <remarks>
/// Files are grouped into chunks that share one compression stream. Reading a file means
/// decoding its chunk from the start and discarding everything before it, so callers that
/// want many files should walk them in chunk and offset order.
/// </remarks>
internal sealed class PayloadReader(IReadOnlyList<InstallerSlice> slices, PayloadCompression compression)
    : IDisposable
{
    private const int SkipBufferSize = 128 * 1024;

    /// <summary>The codec the installer declared for its payload.</summary>
    public PayloadCompression Compression => compression;

    /// <summary>
    /// Opens one file's decompressed contents.
    /// </summary>
    public Stream OpenRead(FileLocation location)
    {
        var chunk = OpenChunk(location);

        try
        {
            SkipExactly(chunk, location.OffsetInChunk);

            return Wrap(chunk, location, leaveOpen: false);
        }
        catch
        {
            chunk.Dispose();

            throw;
        }
    }

    /// <summary>
    /// Opens the decompressed stream of the chunk that holds <paramref name="location"/>,
    /// positioned at the chunk's first byte.
    /// </summary>
    public Stream OpenChunk(FileLocation location)
    {
        var slice = new SliceStream(slices, location.FirstSlice, location.ChunkOffset);

        try
        {
            return ChunkDecoder.Open(
                slice,
                location.IsCompressed ? compression : PayloadCompression.Stored,
                location.ChunkCompressedSize);
        }
        catch
        {
            slice.Dispose();

            throw;
        }
    }

    /// <summary>
    /// Bounds an already-positioned chunk stream to one file, undoing the executable
    /// transform when the file was stored with it applied.
    /// </summary>
    public static Stream Wrap(Stream chunk, FileLocation location, bool leaveOpen)
    {
        Stream bounded = new BoundedStream(chunk, location.OriginalSize, leaveOpen);

        return location.IsFiltered ? new CallInstructionStream(bounded) : bounded;
    }

    /// <summary>
    /// Discards exactly <paramref name="count"/> bytes, failing if the stream runs out.
    /// </summary>
    public static void SkipExactly(Stream stream, long count)
    {
        if (count <= 0)
            return;

        var buffer = new byte[(int)Math.Min(SkipBufferSize, count)];

        while (count > 0)
        {
            var wanted = (int)Math.Min(buffer.Length, count);
            var read = stream.Read(buffer.AsSpan(0, wanted));

            if (read <= 0)
                throw new CorruptInstallerException(
                    "A payload chunk ended earlier than the installer's file table expects.");

            count -= read;
        }
    }

    public void Dispose()
    {
    }
}
