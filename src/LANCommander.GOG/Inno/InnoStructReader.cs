using System.Buffers.Binary;
using System.Text;
using LANCommander.GOG.Inno.Versioning;

namespace LANCommander.GOG.Inno;

/// <summary>
/// Sequential reader over a fully decompressed setup header, providing the primitive
/// types Inno Setup's record layout is built from.
/// </summary>
/// <remarks>
/// The header is read strictly front to back; the final <see cref="Position"/> must land
/// exactly on <see cref="Length"/>, which is the strongest available check that every
/// record was decoded with the correct field layout.
/// </remarks>
internal sealed class InnoStructReader(ReadOnlyMemory<byte> data, InnoVersion version)
{
    private readonly ReadOnlyMemory<byte> _data = data;

    /// <summary>The format version, used to gate version-dependent fields.</summary>
    public InnoVersion Version { get; } = version;

    /// <summary>Current read offset, in bytes, from the start of the decompressed header.</summary>
    public int Position { get; private set; }

    /// <summary>Total size of the decompressed header, in bytes.</summary>
    public int Length => _data.Length;

    /// <summary>Bytes not yet consumed.</summary>
    public int Remaining => Length - Position;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
            throw new CorruptInstallerException(
                $"Setup header ended unexpectedly: wanted {count} bytes at offset {Position} of {Length}.");

        var span = _data.Span.Slice(Position, count);

        Position += count;

        return span;
    }

    /// <summary>
    /// Creates an independent reader positioned <paramref name="skip"/> bytes ahead of this
    /// one, for probing a layout without committing to it.
    /// </summary>
    public InnoStructReader Fork(int skip) => new(_data[(Position + skip)..], Version);

    public byte ReadByte() => Take(1)[0];

    public bool ReadBoolean() => ReadByte() != 0;

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    /// <summary>Reads a raw run of bytes, such as a flag set or a digest.</summary>
    public byte[] ReadBytes(int count) => Take(count).ToArray();

    /// <summary>Skips a run of bytes whose contents are not needed.</summary>
    public void Skip(int count) => Take(count);

    /// <summary>
    /// Reads a length-prefixed string. The prefix is always a byte count, so Unicode
    /// builds store twice as many bytes as characters.
    /// </summary>
    public string ReadString()
    {
        var byteCount = ReadInt32();

        if (byteCount == 0)
            return string.Empty;

        var bytes = Take(byteCount);

        return Version.Unicode
            ? Encoding.Unicode.GetString(bytes)
            : Encoding.Latin1.GetString(bytes);
    }

    /// <summary>
    /// Reads a length-prefixed byte string that stays 8-bit even in Unicode builds.
    /// Inno stores license text, info text, and the compiled script this way.
    /// </summary>
    public byte[] ReadAnsiBytes()
    {
        var byteCount = ReadInt32();

        return byteCount == 0 ? [] : Take(byteCount).ToArray();
    }

    /// <summary>Reads an 8-bit length-prefixed string and decodes it as Latin-1.</summary>
    public string ReadAnsiString() => Encoding.Latin1.GetString(ReadAnsiBytes());

    /// <summary>
    /// Reads a Windows version requirement: a packed Win32 version, an NT version, and
    /// an NT service-pack level.
    /// </summary>
    public WindowsVersionRequirement ReadWindowsVersion()
    {
        var windows = ReadUInt32();
        var nt = ReadUInt32();
        var servicePack = Version.Unicode ? ReadUInt16() : (ushort)0;

        return new WindowsVersionRequirement(windows, nt, servicePack);
    }
}

/// <summary>A minimum or maximum Windows version constraint declared by the installer.</summary>
/// <param name="Windows">Packed Win32 version number, or 0 when unconstrained.</param>
/// <param name="NtVersion">Packed Windows NT version number, or 0 when unconstrained.</param>
/// <param name="NtServicePack">Required NT service pack, encoded as major in the high byte.</param>
public readonly record struct WindowsVersionRequirement(uint Windows, uint NtVersion, ushort NtServicePack)
{
    /// <summary>True when no constraint is expressed.</summary>
    public bool IsUnconstrained => Windows == 0 && NtVersion == 0 && NtServicePack == 0;
}
