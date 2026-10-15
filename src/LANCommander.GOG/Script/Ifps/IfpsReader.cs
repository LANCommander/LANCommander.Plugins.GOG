using System.Buffers.Binary;
using System.Text;

namespace LANCommander.GOG.Script.Ifps;

/// <summary>
/// Sequential reader over a compiled Pascal Script blob, providing the primitive types its
/// record layout is built from.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors <see cref="Inno.InnoStructReader"/> in shape and intent, but the two formats
/// disagree about strings: Inno's setup header stores UTF-16 in Unicode builds, while a
/// compiled script is 8-bit throughout. Keeping them separate avoids a reader that has to be
/// told which convention it is following.
/// </para>
/// <para>
/// Strings are decoded as Latin-1 so that every byte survives the round trip, including the
/// non-ASCII bytes that appear inside procedure-pointer parameter blobs.
/// </para>
/// </remarks>
internal sealed class IfpsReader(ReadOnlyMemory<byte> data)
{
    private readonly ReadOnlyMemory<byte> _data = data;

    /// <summary>Current read offset, in bytes, from the start of the blob.</summary>
    public int Position { get; private set; }

    /// <summary>Total size of the blob, in bytes.</summary>
    public int Length => _data.Length;

    /// <summary>Bytes not yet consumed.</summary>
    public int Remaining => Length - Position;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
            throw new IfpsException(
                $"Compiled script ended unexpectedly: wanted {count} bytes at offset {Position} of {Length}.");

        var span = _data.Span.Slice(Position, count);

        Position += count;

        return span;
    }

    /// <summary>
    /// Moves to an absolute offset. Function bodies are addressed absolutely within the blob,
    /// so decoding one means jumping to where its record says it lives.
    /// </summary>
    public void Seek(int position)
    {
        if (position < 0 || position > Length)
            throw new IfpsException($"Compiled script offset {position} lies outside the {Length}-byte blob.");

        Position = position;
    }

    /// <summary>Creates an independent reader over the same blob, positioned at <paramref name="position"/>.</summary>
    public IfpsReader At(int position)
    {
        var reader = new IfpsReader(_data);

        reader.Seek(position);

        return reader;
    }

    public byte ReadByte() => Take(1)[0];

    public bool ReadBoolean() => ReadByte() != 0;

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    /// <summary>Reads a raw run of bytes, such as a parameter blob or an extended float.</summary>
    public byte[] ReadBytes(int count) => Take(count).ToArray();

    /// <summary>Skips a run of bytes whose contents are not needed.</summary>
    public void Skip(int count) => Take(count);

    /// <summary>
    /// Reads a string whose byte count is given by a 32-bit prefix. Type names, exported
    /// names, and declarations are all stored this way.
    /// </summary>
    public string ReadLongString()
    {
        var byteCount = ReadUInt32();

        if (byteCount == 0)
            return string.Empty;

        if (byteCount > (uint)Remaining)
            throw new IfpsException(
                $"Compiled script declares a {byteCount}-byte string at offset {Position} but only {Remaining} bytes remain.");

        return Encoding.Latin1.GetString(Take((int)byteCount));
    }

    /// <summary>
    /// Reads a string whose byte count is given by a single-byte prefix. Only the names of
    /// imported functions use this form, which is why they are limited to 255 characters.
    /// </summary>
    public string ReadShortString() => Encoding.Latin1.GetString(Take(ReadByte()));

    /// <summary>Reads a UTF-16 string whose character count is given by a 32-bit prefix.</summary>
    public string ReadWideString()
    {
        var charCount = ReadUInt32();

        if (charCount == 0)
            return string.Empty;

        if (charCount > (uint)(Remaining / 2))
            throw new IfpsException(
                $"Compiled script declares a {charCount}-character string at offset {Position} but only {Remaining} bytes remain.");

        return Encoding.Unicode.GetString(Take((int)charCount * 2));
    }
}
