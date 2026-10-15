namespace LANCommander.GOG.Inno.Streams;

/// <summary>
/// Standard CRC-32 (reflected, polynomial 0xEDB88320). Inno Setup uses it to guard the
/// loader offset table, the block header, and every 4 KiB block of the setup header.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            var value = i;

            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;

            table[i] = value;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    public static uint Update(uint seed, ReadOnlySpan<byte> data)
    {
        var crc = ~seed;

        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);

        return ~crc;
    }
}
