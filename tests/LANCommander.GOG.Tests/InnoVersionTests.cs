using System.Text;
using LANCommander.GOG.Inno.Versioning;

namespace LANCommander.GOG.Tests;

public class InnoVersionTests
{
    private static byte[] Identifier(string text)
    {
        var buffer = new byte[InnoVersion.IdentifierLength];

        Encoding.ASCII.GetBytes(text).CopyTo(buffer, 0);

        return buffer;
    }

    [Theory]
    [InlineData("Inno Setup Setup Data (5.5.7) (u)", 5, 5, 7, true)]
    [InlineData("Inno Setup Setup Data (5.5.0) (u)", 5, 5, 0, true)]
    [InlineData("Inno Setup Setup Data (5.6.2) (u)", 5, 6, 2, true)]
    [InlineData("Inno Setup Setup Data (5.5.7)", 5, 5, 7, false)]
    [InlineData("Inno Setup Setup Data (6.1.0) (u)", 6, 1, 0, true)]
    public void ParsesTheVersionsFoundInRealInstallers(
        string text, int major, int minor, int revision, bool unicode)
    {
        var version = InnoVersion.Parse(Identifier(text));

        Assert.Equal(new InnoVersion(major, minor, revision, unicode), version);
    }

    [Fact]
    public void AcceptsAFourthVersionComponent()
    {
        var version = InnoVersion.Parse(Identifier("Inno Setup Setup Data (5.5.7.1) (u)"));

        Assert.Equal(new InnoVersion(5, 5, 7, true), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MZP\0\0\0\0")]
    [InlineData("Inno Setup Messages (5.5.3) (u)")]
    [InlineData("Inno Setup Setup Data (bogus)")]
    public void RejectsAnythingThatIsNotASetupDataHeader(string text)
    {
        Assert.False(InnoVersion.TryParse(Identifier(text), out _, out _));
        Assert.Throws<UnsupportedInstallerException>(() => InnoVersion.Parse(Identifier(text)));
    }

    [Fact]
    public void ComparesVersionsByOrdinal()
    {
        var older = new InnoVersion(5, 5, 0, true);
        var newer = new InnoVersion(5, 5, 7, true);

        Assert.True(newer.AtLeast(5, 5, 6));
        Assert.False(older.AtLeast(5, 5, 6));
        Assert.True(older.Below(5, 6));
        Assert.True(newer.CompareTo(older) > 0);
    }

    [Fact]
    public void TreatsTheMinorVersionAsMoreSignificantThanTheRevision()
    {
        // 5.6.0 must outrank 5.5.9 even though its revision is lower.
        Assert.True(new InnoVersion(5, 6, 0, true).AtLeast(5, 5, 9));
        Assert.False(new InnoVersion(5, 5, 9, true).AtLeast(5, 6));
    }
}
