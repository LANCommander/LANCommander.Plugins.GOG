using LANCommander.GOG.Inno.Loader;

namespace LANCommander.GOG.Inno.Streams;

/// <summary>
/// Presents the payload slices of an installer as one continuous forward-only stream,
/// hiding each slice's own header and the boundaries between files.
/// </summary>
/// <remarks>
/// A compressed chunk may begin in one slice and end in the next, so readers must be able
/// to cross a boundary without noticing it.
/// </remarks>
internal sealed class SliceStream : Stream
{
    private readonly IReadOnlyList<InstallerSlice> _slices;

    private FileStream? _current;
    private int _index;
    private long _remainingInSlice;

    /// <summary>
    /// Opens the payload at the chunk recorded at <paramref name="chunkOffset"/> within
    /// slice <paramref name="firstSlice"/>.
    /// </summary>
    /// <param name="slices">All payload slices, in file-table order.</param>
    /// <param name="firstSlice">0-based slice number the chunk starts in.</param>
    /// <param name="chunkOffset">Chunk offset exactly as recorded in the file table.</param>
    public SliceStream(IReadOnlyList<InstallerSlice> slices, uint firstSlice, long chunkOffset)
    {
        _slices = slices;
        _index = IndexOf(slices, firstSlice);

        OpenAt(_slices[_index].ChunkOrigin + chunkOffset);
    }

    /// <summary>
    /// Maps a slice number from the file table onto the resolved slice list.
    /// </summary>
    /// <remarks>
    /// The file table numbers slices from zero while the files on disk are named from one,
    /// so slice 0 is <c>-1.bin</c>. A payload embedded in the executable is the single slice
    /// zero, which the same indexing covers.
    /// </remarks>
    private static int IndexOf(IReadOnlyList<InstallerSlice> slices, uint sliceNumber)
    {
        if (sliceNumber < slices.Count)
            return (int)sliceNumber;

        throw new MissingSliceException(
            $"The installer refers to payload slice {sliceNumber + 1}, but only {slices.Count} " +
            "were found beside it.",
            $"-{sliceNumber + 1}.bin");
    }

    /// <summary>Opens the current slice positioned at an absolute offset within its file.</summary>
    private void OpenAt(long position)
    {
        _current?.Dispose();

        if (_index >= _slices.Count)
        {
            _current = null;
            _remainingInSlice = 0;

            return;
        }

        var slice = _slices[_index];

        if (position > slice.FileLength)
            throw new CorruptInstallerException(
                $"Payload offset {position} is past the end of \"{slice.FileName}\".");

        _current = File.OpenRead(slice.Path);
        _current.Position = position;
        _remainingInSlice = slice.FileLength - position;
    }

    /// <summary>
    /// Moves to the next slice, where the payload resumes immediately after its header.
    /// </summary>
    private void AdvanceSlice()
    {
        _index++;

        OpenAt(_index < _slices.Count ? _slices[_index].PayloadStart : 0);
    }

    public override int Read(Span<byte> buffer)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            if (_current is null)
                break;

            if (_remainingInSlice <= 0)
            {
                AdvanceSlice();

                continue;
            }

            var wanted = (int)Math.Min(buffer.Length - total, _remainingInSlice);
            var read = _current.Read(buffer.Slice(total, wanted));

            if (read <= 0)
            {
                AdvanceSlice();

                continue;
            }

            total += read;
            _remainingInSlice -= read;
        }

        return total;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        return Read(buffer.AsSpan(offset, count));
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _current?.Dispose();

        base.Dispose(disposing);
    }
}
