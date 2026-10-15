using System.Text.Json;
using LANCommander.GOG.Packaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.GOG.Tests;

/// <summary>
/// Runs the packager's extraction over real installers, which is the only place the two payload
/// layouts can be told apart.
/// </summary>
public class PackagerIntegrationTests
{
    [InstallerFact(SampleInstallers.CloseCombat2)]
    public async Task AnOrdinaryInstallerExtractsEveryFileItDeclares()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.CloseCombat2)!);

        var directory = TempDirectory();

        try
        {
            var result = await GameTreeExtractor.ExtractAsync(
                package, directory, verify: true, NullLogger.Instance);

            Assert.False(result.ContentAddressed);
            Assert.True(result.Verified);
            Assert.Equal(2282, result.FilesWritten);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [InstallerFact(SampleInstallers.WheelOfTime)]
    public async Task AContentAddressedInstallerAssemblesFilesRatherThanBlobs()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.WheelOfTime)!);

        var directory = TempDirectory();

        try
        {
            var result = await GameTreeExtractor.ExtractAsync(
                package, directory, verify: true, NullLogger.Instance);

            Assert.True(result.ContentAddressed);

            // The staged blobs are deflated on top of the payload's own compression - GOG's script
            // calls unzlib_file on each one - so concatenating them verbatim yields zlib streams
            // rather than the game.
            var info = Directory
                .EnumerateFiles(directory, "goggame-*.info", SearchOption.AllDirectories)
                .Single();

            using var document = JsonDocument.Parse(File.ReadAllBytes(info));

            Assert.Equal("The Wheel of Time", document.RootElement.GetProperty("name").GetString());

            var executable = File.ReadAllBytes(Path.Combine(directory, "System", "WoT.exe"));

            Assert.Equal((byte)'M', executable[0]);
            Assert.Equal((byte)'Z', executable[1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lancommander-gog-test-{Guid.NewGuid()}");

        Directory.CreateDirectory(path);

        return path;
    }
}
