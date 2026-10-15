namespace LANCommander.GOG.Inno.Streams;

/// <summary>
/// Reverses the transform Inno Setup applies to executables before compressing them.
/// </summary>
/// <remarks>
/// <para>
/// To help the compressor, the operands of x86 <c>CALL</c> and <c>JMP</c> instructions are
/// rewritten from addresses relative to the next instruction into addresses relative to the
/// start of the buffer, so that repeated calls to one target compress to identical bytes.
/// Only the low three bytes of the operand are rewritten; the high byte is inverted when
/// bit 23 of the result is set, which keeps it zero for both forward and backward jumps.
/// </para>
/// <para>
/// The transform is applied to one fixed-size buffer at a time, and an instruction whose
/// operand would run past the end of a buffer is left alone. Reproducing that buffer size
/// exactly matters: using a different one silently corrupts the instructions that straddle a
/// boundary. Reading such a file without undoing the transform yields a binary that is
/// subtly and invisibly wrong.
/// </para>
/// </remarks>
internal sealed class CallInstructionStream(Stream inner, bool leaveOpen = false) : Stream
{
    /// <summary>
    /// Size of the buffer the transform is applied to, matching Inno Setup's compressor.
    /// Verified against installer checksums: larger buffers produce different output.
    /// </summary>
    private const int BlockSize = 64 * 1024;

    /// <summary>Length of an instruction opcode plus its 32-bit operand.</summary>
    private const int InstructionLength = 5;

    private readonly byte[] _buffer = new byte[BlockSize];

    /// <summary>Bytes of the current block held in <see cref="_buffer"/>.</summary>
    private int _length;

    /// <summary>Read cursor within the current block.</summary>
    private int _offset;

    /// <summary>Offset of the current block from the start of the file.</summary>
    private long _blockStart;

    private bool _endOfInner;

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        if (_offset >= _length && !FillBlock())
            return 0;

        var count = Math.Min(buffer.Length, _length - _offset);

        _buffer.AsSpan(_offset, count).CopyTo(buffer);

        _offset += count;

        return count;
    }

    /// <summary>
    /// Reads the next whole block and untransforms it.
    /// </summary>
    private bool FillBlock()
    {
        if (_endOfInner)
            return false;

        _blockStart += _length;
        _offset = 0;
        _length = 0;

        // Blocks must line up with the ones the compressor used, so a short read from the
        // inner stream is topped up rather than treated as a block of its own.
        while (_length < _buffer.Length)
        {
            var read = inner.Read(_buffer.AsSpan(_length));

            if (read <= 0)
            {
                _endOfInner = true;

                break;
            }

            _length += read;
        }

        if (_length == 0)
            return false;

        Untransform(_buffer.AsSpan(0, _length), _blockStart);

        return true;
    }

    /// <summary>
    /// Converts buffer-relative operands back into operands relative to the next instruction.
    /// </summary>
    /// <param name="block">The block to rewrite in place.</param>
    /// <param name="blockStart">Offset of the block from the start of the file.</param>
    private static void Untransform(Span<byte> block, long blockStart)
    {
        if (block.Length < InstructionLength)
            return;

        // An instruction is only converted when its whole operand lies inside the block.
        var limit = block.Length - 4;
        var i = 0;

        while (i < limit)
        {
            if (block[i] is not (0xE8 or 0xE9))
            {
                i++;

                continue;
            }

            i++;

            // A high byte other than 0x00 or 0xFF means this was not really an instruction.
            if (block[i + 3] is 0x00 or 0xFF)
            {
                var address = (uint)(blockStart + i + 4) & 0xFFFFFF;
                var relative = (uint)(block[i] | (block[i + 1] << 8) | (block[i + 2] << 16));

                relative -= address;

                // The high byte holds the sign extension of bit 23, inverted by the encoder
                // so that it stays zero whichever way the jump goes.
                if ((relative & 0x800000) != 0)
                    block[i + 3] = (byte)~block[i + 3];

                block[i] = (byte)relative;
                block[i + 1] = (byte)(relative >> 8);
                block[i + 2] = (byte)(relative >> 16);
            }

            i += 4;
        }
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
        get => _blockStart + _offset;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
            inner.Dispose();

        base.Dispose(disposing);
    }
}
