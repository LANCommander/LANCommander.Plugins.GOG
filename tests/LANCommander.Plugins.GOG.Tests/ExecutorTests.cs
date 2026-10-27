using System.IO.Compression;
using System.Net;
using LANCommander.Plugins.GOG.Api;
using LANCommander.Plugins.GOG.Jobs;
using LANCommander.Plugins.GOG.Storage;
using LANCommander.Plugins.GOG.Uploads;
using LANCommander.SDK.Enums;
using LANCommander.SDK.Helpers;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Sdk = LANCommander.SDK.Models.Manifest;

namespace LANCommander.Plugins.GOG.Tests;

public class InstallerSelectionTests
{
    private static GogInstallerDownload Installer(string os, string language) => new()
    {
        Os = os,
        Language = language,
        Files = [new GogInstallerFile { Id = "1", Size = 1, Downlink = "https://api.gog.com/x" }],
    };

    [Fact]
    public void TheRequestedLanguageIsPreferred()
    {
        var chosen = GogImportExecutor.SelectInstaller(
            [Installer("windows", "en"), Installer("windows", "de"), Installer("linux", "de")], "de");

        Assert.Equal(("windows", "de"), (chosen!.Os, chosen.Language));
    }

    [Fact]
    public void EnglishIsTheFallback()
    {
        var chosen = GogImportExecutor.SelectInstaller(
            [Installer("windows", "fr"), Installer("windows", "en")], "pl");

        Assert.Equal("en", chosen!.Language);
    }

    [Fact]
    public void AnyLanguageIsBetterThanNone()
    {
        var chosen = GogImportExecutor.SelectInstaller([Installer("windows", "fr")], "pl");

        Assert.Equal("fr", chosen!.Language);
    }

    [Fact]
    public void OnlyWindowsInstallersAreConsidered()
        => Assert.Null(GogImportExecutor.SelectInstaller([Installer("linux", "en"), Installer("mac", "en")], "en"));
}

public class ExecutorTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task AGameWithoutAWindowsInstallerFailsWithAReason()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"id":123,"title":"Linux Only","downloads":{"installers":[{"id":"installer_linux_en","os":"linux","language":"en","files":[{"id":"en3installer0","size":10,"downlink":"https://api.gog.com/products/123/downlink/installer/en3installer0"}]}]}}"""));

        var (executor, accountId, _, _) = Build(handler, new RecordingImporter());

        var exception = await Assert.ThrowsAsync<GogImportException>(
            () => executor.ExecuteAsync(Job(accountId), new NullReporter(), CancellationToken.None));

        Assert.Equal("GOG offers no Windows installer for this game, only linux.", exception.Message);
    }

    /// <summary>
    /// Runs a whole import against a fake GOG serving a real installer, stopping short of the host's
    /// import pipeline.
    /// </summary>
    [InstallerFact(SampleInstallers.CloseCombat2)]
    public async Task AnInstallerIsDownloadedPackagedAndImported()
    {
        var installer = SampleInstallers.Find(SampleInstallers.CloseCombat2)!;
        var size = new FileInfo(installer).Length;
        var fileName = Path.GetFileName(installer);

        var handler = new FakeHttpHandler(request =>
        {
            var url = request.RequestUri!;

            if (url.Host == "api.gog.com" && url.AbsolutePath == "/products/1207658827")
                return FakeHttpHandler.Json($$$"""
                    {"id":1207658827,"title":"Close Combat 2","downloads":{"installers":[
                      {"id":"installer_windows_en","name":"Close Combat 2","os":"windows","language":"en","language_full":"English","version":"2.0b","total_size":{{{size}}},
                       "files":[{"id":"en1installer0","size":{{{size}}},"downlink":"https://api.gog.com/products/1207658827/downlink/installer/en1installer0"}]}]}}
                    """);

            if (url.Host == "api.gog.com" && url.AbsolutePath.Contains("/downlink/"))
                return FakeHttpHandler.Json($$"""{"downlink":"https://cdn.example/secure/{{Uri.EscapeDataString(fileName)}}?token=1","checksum":""}""");

            if (url.Host == "cdn.example")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(installer)) };

            return FakeHttpHandler.NotFound();
        });

        var importer = new RecordingImporter();
        var reporter = new RecordingReporter();
        var redistributable = new RedistributableInfo { Id = Guid.NewGuid(), Name = "DirectX" };
        var (executor, accountId, imports, settings) = Build(handler, importer, redistributable);

        settings.Save(settings.Current with { Redistributables = [redistributable.Id, Guid.NewGuid()] });

        var outcome = await executor.ExecuteAsync(Job(accountId, "1207658827", "Close Combat 2"), reporter, CancellationToken.None);

        Assert.Equal(importer.GameId, outcome.GameId);

        // The package reached the importer whole: a manifest naming the game, with one archive.
        var manifest = Assert.IsType<Sdk.Game>(importer.Manifest);

        Assert.Equal("Close Combat 2", manifest.Title);
        Assert.Single(manifest.Archives);
        Assert.Contains(manifest.ExternalIds, external => external.Provider == "GOG");
        Assert.True(importer.ArchiveEntries > 2000);

        // The redistributable chosen in the settings is linked; the one the server no longer has is not.
        Assert.Equal((redistributable.Id, redistributable.Name), manifest.Redistributables.Select(linked => (linked.Id, linked.Name)).Single());
        Assert.Single(reporter.Warnings, warning => warning.Contains("no longer on the server"));

        Assert.Equal(
            [GogImportStage.Downloading, GogImportStage.Extracting, GogImportStage.Packaging, GogImportStage.Importing],
            reporter.Stages);

        // The import is remembered, so the next one updates the same game.
        Assert.Equal(importer.GameId, imports.Get("1207658827")!.GameId);

        // Nothing is left behind in the work directory.
        Assert.Empty(Directory.EnumerateFileSystemEntries(settings.WorkDirectory));
    }

    [Fact]
    public async Task AnUploadThatHoldsNoInstallerFailsWithAReason()
    {
        var (executor, _, _, settings) = Build(new FakeHttpHandler(_ => FakeHttpHandler.NotFound()), new RecordingImporter());
        var uploads = new GogUploadStore(settings, NullLogger<GogUploadStore>.Instance);
        var uploadId = Guid.NewGuid();

        await uploads.SaveAsync(uploadId, "setup_game-1.bin", new MemoryStream([1, 2, 3]), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<GogImportException>(
            () => executor.ExecuteAsync(UploadJob(uploadId), new NullReporter(), CancellationToken.None));

        Assert.Contains("No installer has been uploaded", exception.Message);

        // The upload is kept, so the missing file can be added and the import retried.
        Assert.Single(uploads.Files(uploadId));
    }

    /// <summary>
    /// Imports an installer as if an administrator had uploaded it: nothing is asked of GOG, and the
    /// game is identified from the installer itself.
    /// </summary>
    [InstallerFact(SampleInstallers.CloseCombat2)]
    public async Task AnUploadedInstallerIsPackagedAndImported()
    {
        var installer = SampleInstallers.Find(SampleInstallers.CloseCombat2)!;
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.NotFound());
        var importer = new RecordingImporter();
        var reporter = new RecordingReporter();
        var (executor, _, imports, settings) = Build(handler, importer);
        var uploads = new GogUploadStore(settings, NullLogger<GogUploadStore>.Instance);
        var uploadId = Guid.NewGuid();

        await using (var content = File.OpenRead(installer))
            await uploads.SaveAsync(uploadId, Path.GetFileName(installer), content, CancellationToken.None);

        var outcome = await executor.ExecuteAsync(UploadJob(uploadId), reporter, CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.Equal(importer.GameId, outcome.GameId);
        Assert.Equal("Close Combat 2", Assert.IsType<Sdk.Game>(importer.Manifest).Title);
        Assert.Equal("1207658827", reporter.Identity?.ProductId);
        Assert.Equal([GogImportStage.Extracting, GogImportStage.Packaging, GogImportStage.Importing], reporter.Stages);

        // Remembered under the product the installer names, so a later import from GOG updates it.
        Assert.Equal(importer.GameId, imports.Get("1207658827")!.GameId);

        // The upload is gone with the rest of the work files.
        Assert.Empty(Directory.EnumerateFiles(settings.WorkDirectory, "*", SearchOption.AllDirectories));
    }

    private static GogImportJobSnapshot UploadJob(Guid uploadId) => new()
    {
        Id = Guid.NewGuid(),
        Title = "setup_game",
        UploadId = uploadId,
    };

    private static GogImportJobSnapshot Job(Guid accountId, string productId = "123", string title = "Test Game") => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        ProductId = productId,
        Title = title,
        Language = "en",
    };

    private (GogImportExecutor Executor, Guid AccountId, GogImportMapStore Imports, GogSettingsStore Settings) Build(
        FakeHttpHandler handler,
        IPackageImporter importer,
        params RedistributableInfo[] redistributables)
    {
        var paths = _directory.PluginPaths;
        var accounts = TestServices.Accounts(paths);
        var account = TestServices.LinkedAccount(accounts);
        var settings = new GogSettingsStore(paths);
        var imports = new GogImportMapStore(paths);

        Directory.CreateDirectory(settings.WorkDirectory);

        var executor = new GogImportExecutor(
            TestServices.Api(accounts, handler),
            new GogDownloader(new HttpClient(handler), NullLogger<GogDownloader>.Instance) { RetryDelay = _ => TimeSpan.Zero },
            settings,
            imports,
            new GogUploadStore(settings, NullLogger<GogUploadStore>.Instance),
            importer,
            new FixedRedistributables(redistributables),
            NullLogger<GogImportExecutor>.Instance);

        return (executor, account.Id, imports, settings);
    }

    private sealed class NullReporter : IGogImportReporter
    {
        public void Stage(GogImportStage stage, string? message = null) { }
        public void Progress(long completed, long total, string? message = null) { }
        public void Installer(string name, string? version) { }
        public void Identified(string? productId, string title) { }
        public void Warning(string warning) { }
    }

    private sealed class RecordingReporter : IGogImportReporter
    {
        public List<GogImportStage> Stages { get; } = [];
        public List<string> Warnings { get; } = [];
        public (string? ProductId, string Title)? Identity { get; private set; }

        public void Stage(GogImportStage stage, string? message = null) => Stages.Add(stage);
        public void Progress(long completed, long total, string? message = null) { }
        public void Installer(string name, string? version) { }
        public void Identified(string? productId, string title) => Identity = (productId, title);
        public void Warning(string warning) => Warnings.Add(warning);
    }

    /// <summary>Opens the package as the server would, and records what it found.</summary>
    private sealed class RecordingImporter : IPackageImporter
    {
        public Guid GameId { get; } = Guid.NewGuid();
        public object? Manifest { get; private set; }
        public int ArchiveEntries { get; private set; }

        public Task<PackageImportResult> ImportStreamAsync(Stream package, Guid? storageLocationId = null, ManifestType? manifestType = null, IProgress<long>? copyProgress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageImportResult> ImportFileAsync(string packagePath, Guid? storageLocationId = null, ManifestType? manifestType = null, CancellationToken cancellationToken = default)
        {
            using var package = ZipFile.OpenRead(packagePath);

            using (var reader = new StreamReader(package.GetEntry(ManifestHelper.ManifestFilename)!.Open()))
                Manifest = ManifestHelper.Deserialize<Sdk.Game>(reader.ReadToEnd());

            var game = (Sdk.Game)Manifest!;
            var archive = Path.Combine(Path.GetDirectoryName(packagePath)!, "inspected-archive.zip");

            // The inner archive is read from a file: a ZIP needs a seekable stream.
            package.GetEntry($"Archives/{game.Archives.Single().Id}")!.ExtractToFile(archive);

            try
            {
                using var inner = ZipFile.OpenRead(archive);

                ArchiveEntries = inner.Entries.Count;
            }
            finally
            {
                File.Delete(archive);
            }

            return Task.FromResult(new PackageImportResult(GameId, ManifestType.Game, 1));
        }
    }
}
