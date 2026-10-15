namespace LANCommander.GOG.Inno.Streams;

/// <summary>
/// Exposes at most a fixed number of bytes from the front of another stream.
/// </summary>
/// <remarks>
/// Files inside a payload chunk are stored back to back, so a reader must be stopped at the
/// end of the file it asked for rather than running on into the next one.
/// </remarks>
internal sealed class BoundedStream(Stream inner, long length, bool leaveOpen = false) : Stream
{
    private long _position;

    public override int Read(Span<byte> buffer)
    {
        var wanted = (int)Math.Min(buffer.Length, length - _position);

        if (wanted <= 0)
            return 0;

        var read = inner.Read(buffer[..wanted]);

        _position += read;

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);

        return Read(buffer.AsSpan(offset, count));
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
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
