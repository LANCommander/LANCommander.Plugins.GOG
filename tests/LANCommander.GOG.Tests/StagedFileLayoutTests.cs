using LANCommander.GOG.Gog;
using LANCommander.GOG.Manifest;

namespace LANCommander.GOG.Tests;

/// <summary>
/// Covers the recovery of where a content-addressed payload's blobs belong.
/// </summary>
public class StagedFileLayoutTests
{
    [Fact]
    public void ReadsTheDestinationOutOfTheInstallHook()
    {
        var staged = StagedFileLayout.Resolve([
            Entry(0, @"{tmp}/b5/54\b5540536651542b104eb0c2166b8f36b",
                "before_install('63c703a7a0313f495484dd2338a284ab', 'directplay.cmd', 1)"),
        ]);

        var file = Assert.Single(staged);

        Assert.Equal("directplay.cmd", file.DestinationPath);
        Assert.Equal("63c703a7a0313f495484dd2338a284ab", file.Id);
        Assert.Equal([0], file.FileIndices);
        Assert.False(file.IsChunked);
        Assert.False(file.IsDependency);
    }

    [Fact]
    public void ClaimsTheFollowingEntriesForAFileSplitIntoChunks()
    {
        var staged = StagedFileLayout.Resolve([
            Entry(0, "{tmp}/a", "before_install('id1', 'Movies\\Intro.mov', 3)"),
            Entry(1, "{tmp}/b", string.Empty),
            Entry(2, "{tmp}/c", string.Empty),
            Entry(3, "{tmp}/d", "before_install('id2', 'readme.txt', 1)"),
        ]);

        Assert.Equal(2, staged.Count);

        Assert.Equal(@"Movies\Intro.mov", staged[0].DestinationPath);
        Assert.Equal([0, 1, 2], staged[0].FileIndices);
        Assert.True(staged[0].IsChunked);

        // The next hook is picked up after the chunks it claimed, not inside them.
        Assert.Equal("readme.txt", staged[1].DestinationPath);
        Assert.Equal([3], staged[1].FileIndices);
    }

    [Fact]
    public void SeparatesRedistributablesFromGameFiles()
    {
        var staged = StagedFileLayout.Resolve([
            Entry(0, "{tmp}/a", "before_install('id1', 'game.exe', 1)"),
            Entry(1, "{tmp}/b",
                "before_install_dependency('id2', '__redist\\DirectX\\d3dx9.cab', 1)"),
        ]);

        Assert.False(staged[0].IsDependency);
        Assert.True(staged[1].IsDependency);
    }

    [Fact]
    public void IgnoresEntriesWhoseHookIsSomethingElse()
    {
        var staged = StagedFileLayout.Resolve([
            Entry(0, "{app}/a.exe", string.Empty),
            Entry(1, "{app}/b.exe", "SomeOtherProcedure"),
            Entry(2, "{app}/c.exe", "before_install('id', 'x'"),
        ]);

        Assert.Empty(staged);
    }

    [InstallerFact(SampleInstallers.WheelOfTime)]
    public void RecoversTheLayoutOfARealContentAddressedInstaller()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.WheelOfTime)!);

        var manifest = package.Manifest;

        Assert.True(manifest.UsesContentAddressedPayload);

        var staged = manifest.StagedFiles;

        Assert.NotEmpty(staged);
        Assert.Contains(staged, file => file.DestinationPath.EndsWith(".wot", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(staged, file => file.IsChunked);
        Assert.Contains(staged, file => file.IsDependency);

        // Every blob belongs to exactly one destination, and every destination is a real path.
        var claimed = staged.SelectMany(file => file.FileIndices).ToList();

        Assert.Equal(claimed.Count, claimed.Distinct().Count());
        Assert.All(staged, file => Assert.DoesNotContain('{', file.DestinationPath));
        Assert.All(staged, file => Assert.NotEmpty(file.DestinationPath));
    }

    [InstallerFact(SampleInstallers.CloseCombat2)]
    public void FindsNothingStagedInAnOrdinaryInstaller()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.CloseCombat2)!);

        Assert.False(package.Manifest.UsesContentAddressedPayload);
        Assert.Empty(package.Manifest.StagedFiles);
    }

    [InstallerFact(SampleInstallers.WheelOfTime)]
    public void ReadsTheInstallHooksInTheOrderTheyAreStored()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.WheelOfTime)!);

        // The two hooks are stored after-then-before. Reading them the other way round puts
        // after_install into BeforeInstall on every entry, which is how the swap shows up.
        var hooked = package.Files.Where(file => file.Conditions.BeforeInstall.Length > 0).ToList();

        Assert.NotEmpty(hooked);
        Assert.All(hooked, file => Assert.StartsWith("before_install", file.Conditions.BeforeInstall, StringComparison.OrdinalIgnoreCase));
        Assert.All(
            hooked.Where(file => file.Conditions.AfterInstall.Length > 0),
            file => Assert.StartsWith("after_install", file.Conditions.AfterInstall, StringComparison.OrdinalIgnoreCase));
    }

    private static InstallerFileEntry Entry(int index, string destination, string beforeInstall) => new(
        Index: index,
        DestinationPath: destination,
        SourcePath: string.Empty,
        Location: null,
        Attributes: 0,
        Kind: InstallerFileKind.File,
        IsExternal: false,
        RawOptions: 0,
        Conditions: EntryConditions.None with { BeforeInstall = beforeInstall });
}
