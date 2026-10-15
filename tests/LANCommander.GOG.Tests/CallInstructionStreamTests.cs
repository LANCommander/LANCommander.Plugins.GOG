using LANCommander.GOG.Inno.Streams;

namespace LANCommander.GOG.Tests;

/// <summary>
/// Exercises the reversal of Inno Setup's executable transform against an encoder written
/// here, so a change to either direction shows up as a failed round trip.
/// </summary>
public class CallInstructionStreamTests
{
    private const int BlockSize = 64 * 1024;

    /// <summary>
    /// Applies the forward transform, mirroring what Inno Setup's compressor does.
    /// </summary>
    private static byte[] Encode(byte[] original)
    {
        var data = (byte[])original.Clone();

        for (var blockStart = 0; blockStart < data.Length; blockStart += BlockSize)
        {
            var length = Math.Min(BlockSize, data.Length - blockStart);

            if (length < 5)
                break;

            var block = data.AsSpan(blockStart, length);
            var limit = length - 4;
            var i = 0;

            while (i < limit)
            {
                if (block[i] is not (0xE8 or 0xE9))
                {
                    i++;

                    continue;
                }

                i++;

                if (block[i + 3] is 0x00 or 0xFF)
                {
                    var address = (uint)(blockStart + i + 4) & 0xFFFFFF;
                    var relative = (uint)(block[i] | (block[i + 1] << 8) | (block[i + 2] << 16));

                    if ((relative & 0x800000) != 0)
                        block[i + 3] = (byte)~block[i + 3];

                    relative += address;

                    block[i] = (byte)relative;
                    block[i + 1] = (byte)(relative >> 8);
                    block[i + 2] = (byte)(relative >> 16);
                }

                i += 4;
            }
        }

        return data;
    }

    private static byte[] Decode(byte[] transformed)
    {
        using var source = new MemoryStream(transformed);
        using var filter = new CallInstructionStream(source);
        using var output = new MemoryStream();

        filter.CopyTo(output);

        return output.ToArray();
    }

    /// <summary>
    /// Builds something shaped like x86 code: mostly filler, with call and jump instructions
    /// at deterministic positions.
    /// </summary>
    private static byte[] BuildCodeLike(int length, int seed)
    {
        var random = new Random(seed);
        var data = new byte[length];

        random.NextBytes(data);

        for (var i = 0; i + 5 < length; i += 37)
        {
            data[i] = (i % 2 == 0) ? (byte)0xE8 : (byte)0xE9;

            // A relative operand whose high byte is the sign extension of bit 23.
            var relative = random.Next(-0x400000, 0x400000);

            data[i + 1] = (byte)relative;
            data[i + 2] = (byte)(relative >> 8);
            data[i + 3] = (byte)(relative >> 16);
            data[i + 4] = relative < 0 ? (byte)0xFF : (byte)0x00;
        }

        return data;
    }

    [Theory]
    [InlineData(64)]
    [InlineData(4096)]
    [InlineData(BlockSize - 1)]
    [InlineData(BlockSize)]
    [InlineData(BlockSize + 1)]
    [InlineData(BlockSize * 3 + 12345)]
    public void DecodingUndoesEncoding(int length)
    {
        var original = BuildCodeLike(length, seed: length);

        Assert.Equal(original, Decode(Encode(original)));
    }

    [Fact]
    public void LeavesDataWithoutInstructionsUntouched()
    {
        var data = new byte[1000];

        Array.Fill(data, (byte)0x42);

        Assert.Equal(data, Decode(data));
    }

    [Fact]
    public void HandlesInputShorterThanOneInstruction()
    {
        byte[] data = [0xE8, 0x01, 0x02];

        Assert.Equal(data, Decode(data));
    }

    /// <summary>
    /// The transform is applied per block, so an instruction straddling a block boundary is
    /// left alone. Getting this wrong corrupts a handful of bytes per 64 KiB, which a naive
    /// round trip over a single block would not catch.
    /// </summary>
    [Fact]
    public void LeavesInstructionsStraddlingABlockBoundaryAlone()
    {
        var data = new byte[BlockSize * 2];

        Array.Fill(data, (byte)0x90);

        // Place a call whose operand runs past the end of the first block.
        data[BlockSize - 2] = 0xE8;
        data[BlockSize - 1] = 0x11;
        data[BlockSize + 0] = 0x22;
        data[BlockSize + 1] = 0x33;
        data[BlockSize + 2] = 0x00;

        var encoded = Encode(data);

        Assert.Equal(data, encoded);
        Assert.Equal(data, Decode(encoded));
    }

    [Fact]
    public void ReadsCorrectlyWhenTheReaderAsksForOneByteAtATime()
    {
        var original = BuildCodeLike(BlockSize + 500, seed: 7);
        var encoded = Encode(original);

        using var source = new MemoryStream(encoded);
        using var filter = new CallInstructionStream(source);

        var output = new List<byte>();
        var buffer = new byte[1];

        while (filter.Read(buffer, 0, 1) == 1)
            output.Add(buffer[0]);

        Assert.Equal(original, output.ToArray());
    }
}
