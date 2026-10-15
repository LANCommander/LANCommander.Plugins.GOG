using LANCommander.GOG.Inno.Streams;

namespace LANCommander.GOG.Tests;

public class Crc32Tests
{
    [Fact]
    public void MatchesTheStandardCheckValue()
    {
        // The reference check value for CRC-32 over "123456789".
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    [Fact]
    public void AnEmptyInputHashesToZero()
    {
        Assert.Equal(0u, Crc32.Compute([]));
    }

    [Fact]
    public void UpdateContinuesAPreviousComputation()
    {
        var whole = Crc32.Compute("hello world"u8);
        var split = Crc32.Update(Crc32.Compute("hello "u8), "world"u8);

        Assert.Equal(whole, split);
    }
}

public class BoundedStreamTests
{
    [Fact]
    public void StopsAtTheGivenLength()
    {
        using var source = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        using var bounded = new BoundedStream(source, 3, leaveOpen: true);
        using var output = new MemoryStream();

        bounded.CopyTo(output);

        Assert.Equal<byte[]>([1, 2, 3], output.ToArray());

        // The underlying stream must stay positioned for whatever is read next.
        Assert.Equal(3, source.Position);
    }

    [Fact]
    public void ReportsLengthAndPosition()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var bounded = new BoundedStream(source, 2);

        Assert.Equal(2, bounded.Length);
        Assert.Equal(0, bounded.Position);

        bounded.ReadExactly(new byte[2]);

        Assert.Equal(2, bounded.Position);
        Assert.Equal(0, bounded.Read(new byte[1], 0, 1));
    }

    [Fact]
    public void ReturnsNothingWhenBoundedToZero()
    {
        using var source = new MemoryStream([1, 2, 3]);
        using var bounded = new BoundedStream(source, 0);

        Assert.Equal(0, bounded.Read(new byte[4], 0, 4));
    }

    [Fact]
    public void LeaveOpenControlsDisposalOfTheInnerStream()
    {
        var source = new MemoryStream([1, 2, 3]);

        new BoundedStream(source, 1, leaveOpen: true).Dispose();

        Assert.Equal(3, source.Length);

        new BoundedStream(source, 1).Dispose();

        Assert.Throws<ObjectDisposedException>(() => source.Length);
    }
}
