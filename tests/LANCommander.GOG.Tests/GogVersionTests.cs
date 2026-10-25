using LANCommander.GOG.Gog;

namespace LANCommander.GOG.Tests;

public class GogVersionTests
{
    [Theory]
    [InlineData("2.0b (17798)", "2.0b")]
    [InlineData("4.13.4m_(14395)", "4.13.4m")]
    [InlineData("gog-1 (17797)", "gog-1")]
    [InlineData("1.28.3e", "1.28.3e")]
    [InlineData("1.0 (64bit)", "1.0 (64bit)")]
    [InlineData("(17798)", "(17798)")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void TheBuildNumberIsStripped(string? version, string? expected)
        => Assert.Equal(expected, GogVersion.Clean(version));
}
