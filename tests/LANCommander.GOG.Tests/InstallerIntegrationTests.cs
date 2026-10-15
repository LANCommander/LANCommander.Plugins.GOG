using System.Security.Cryptography;
using LANCommander.GOG.Gog;
using LANCommander.GOG.Manifest;

namespace LANCommander.GOG.Tests;

/// <summary>
/// Reads real GOG installers end to end. These are the tests that would catch a format
/// misunderstanding; the unit tests above only cover the pieces in isolation.
/// </summary>
public class InstallerIntegrationTests
{
    [InstallerFact(SampleInstallers.CloseCombat2)]
    public void ReadsAnEmbeddedPayloadInstaller()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.CloseCombat2)!);

        var manifest = package.Manifest;

        Assert.Equal(new Inno.Versioning.InnoVersion(5, 5, 7, true), manifest.InnoVersion);
        Assert.Equal("Close Combat 2", manifest.AppName);
        Assert.Equal("1649233263", manifest.ProductId);
        Assert.Equal(Inno.Streams.PayloadCompression.Lzma2, manifest.Compression);

        // The payload lives inside the executable, so there is exactly one slice.
        Assert.Single(package.Slices);
        Assert.False(package.Slices[0].IsExternal);

        Assert.Contains(
            manifest.RegistryEntries,
            entry => entry.Root == RegistryRoot.LocalMachine
                     && entry.Subkey == @"SOFTWARE\GOG.com\Games\1649233263"
                     && entry.ValueName == "ver");
    }

    [InstallerFact(SampleInstallers.Il2Sturmovik)]
    public void ReadsAnInstallerWhosePayloadIsSplitAcrossSlices()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.Il2Sturmovik)!);

        Assert.Equal("1207658749", package.Manifest.ProductId);
        Assert.Equal(2, package.Slices.Count);
        Assert.All(package.Slices, slice => Assert.True(slice.IsExternal));

        // Slice numbering starts at zero even though the files are named from one.
        Assert.Equal([0, 1], package.Slices.Select(slice => slice.Number));

        // Some files must actually live in the second slice, or this proves nothing.
        Assert.Contains(package.Files, file => file.Location?.FirstSlice == 1);
    }

    [InstallerFact(SampleInstallers.WheelOfTime)]
    public void ReadsTheNewerFormatVersion()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.WheelOfTime)!);

        Assert.Equal(new Inno.Versioning.InnoVersion(5, 6, 2, true), package.Manifest.InnoVersion);
        Assert.Equal("1584652180", package.Manifest.ProductId);

        // This build runs a redistributable installer after copying files.
        Assert.Contains(package.Manifest.RunEntries, run => run.Target.Contains("DXSETUP", StringComparison.OrdinalIgnoreCase));
    }

    [InstallerFact(SampleInstallers.CloseCombat2)]
    public void CapturesTheCompiledScriptWithoutRunningIt()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.CloseCombat2)!);

        var scripts = package.Manifest.Scripts;

        Assert.True(scripts.HasCompiledCode);
        Assert.True(scripts.CompiledCodeIsRecognised);
        Assert.True(scripts.CompiledCode.Length > 100_000);
    }

    [InstallerFact(SampleInstallers.CloseCombat2)]
    public async Task ReadsGogMetadataOutOfThePayload()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.CloseCombat2)!);

        var scripts = await package.LoadPayloadScriptsAsync(CancellationToken.None);

        var info = scripts.PayloadScripts.FirstOrDefault(script => script.FileName.EndsWith(".info"));

        Assert.NotNull(info);

        var parsed = GogGameInfo.Parse(info!.Content);

        Assert.NotNull(parsed);
        Assert.Equal("1649233263", parsed!.GameId);
        Assert.Equal("Close Combat 2", parsed.Name);
        Assert.Equal("cc2.exe", parsed.PrimaryPlayTask?.Path);
    }

    /// <summary>
    /// Decompresses a sample of files and checks them against the digests recorded in the
    /// installer. This is the strongest available check: it catches chunk framing, offset,
    /// codec, and executable-transform errors at once.
    /// </summary>
    [InstallerFact(SampleInstallers.CloseCombat2)]
    public void ExtractedFilesMatchTheirRecordedChecksums()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.CloseCombat2)!);

        var candidates = package.Files
            .Where(file => file.HasContent && file.Size is > 0 and < 4 * 1024 * 1024)
            .ToList();

        // Cover both transformed executables and ordinary data files.
        var sample = candidates.Where(file => file.Location!.IsFiltered).Take(5)
            .Concat(candidates.Where(file => !file.Location!.IsFiltered).Take(5))
            .ToList();

        Assert.Equal(10, sample.Count);

        foreach (var entry in sample)
        {
            using var stream = package.OpenRead(entry);
            using var buffer = new MemoryStream();

            stream.CopyTo(buffer);

            Assert.Equal(entry.Size, buffer.Length);
            Assert.Equal(entry.Sha1Hex, Convert.ToHexStringLower(SHA1.HashData(buffer.ToArray())));
        }
    }

    [InstallerFact(SampleInstallers.CloseCombat2)]
    public void TransformedExecutablesDecodeToRealPortableExecutables()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.CloseCombat2)!);

        var executable = package.Files.First(file =>
            file.HasContent
            && file.Location!.IsFiltered
            && file.DestinationPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        using var stream = package.OpenRead(executable);

        var header = new byte[2];

        stream.ReadExactly(header);

        Assert.Equal("MZ"u8.ToArray(), header);
    }

    [InstallerFact(SampleInstallers.Rayman2)]
    public void IdentifiesInstallersWithoutFullyParsingThem()
    {
        var identity = InstallerInspector.Identify(SampleInstallers.Find(SampleInstallers.Rayman2)!);

        Assert.Equal(5, identity.Version.Major);
        Assert.True(identity.Version.Unicode);
        Assert.False(identity.UsesExternalSlices);

        // GOG's fork uses its own loader signature, which stock Inno checks would reject.
        Assert.StartsWith("72446c507453", identity.LoaderSignatureHex);
    }

    [Fact]
    public void RejectsFilesThatAreNotInstallers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"not-an-installer-{Guid.NewGuid():N}.exe");

        File.WriteAllBytes(path, new byte[64 * 1024]);

        try
        {
            Assert.Throws<UnsupportedInstallerException>(() => GogInstaller.Open(path));
            Assert.False(GogInstaller.TryOpen(path, out var installer));
            Assert.Null(installer);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
