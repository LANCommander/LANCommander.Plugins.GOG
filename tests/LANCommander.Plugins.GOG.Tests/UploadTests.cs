using System.Net.Http.Headers;
using Bunit;
using LANCommander.Plugins.GOG.Jobs;
using LANCommander.Plugins.GOG.Pages;
using LANCommander.Plugins.GOG.Storage;
using LANCommander.Plugins.GOG.Uploads;
using LANCommander.SDK.Enums;
using LANCommander.Server.Plugins;
using LANCommander.Server.UI.Controls;
using LANCommander.Server.UI.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.Plugins.GOG.Tests;

public class UploadStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly GogUploadStore _uploads;

    public UploadStoreTests()
    {
        _uploads = new GogUploadStore(new GogSettingsStore(_directory.PluginPaths), NullLogger<GogUploadStore>.Instance);
    }

    public void Dispose() => _directory.Dispose();

    private Task<GogUploadedFile> SaveAsync(Guid uploadId, string name, int size = 4)
        => _uploads.SaveAsync(uploadId, name, new MemoryStream(new byte[size]), CancellationToken.None);

    [Fact]
    public async Task AnInstallerAndItsSlicesAreKeptTogether()
    {
        var uploadId = Guid.NewGuid();

        await SaveAsync(uploadId, "setup_game.exe", 10);
        await SaveAsync(uploadId, "setup_game-1.bin", 20);

        Assert.Equal(
            [new GogUploadedFile("setup_game-1.bin", 20), new GogUploadedFile("setup_game.exe", 10)],
            _uploads.Files(uploadId));

        Assert.Equal(Path.Combine(_uploads.DirectoryFor(uploadId), "setup_game.exe"), _uploads.FindInstaller(uploadId));
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("setup_game.exe.part")]
    [InlineData(".exe")]
    [InlineData("")]
    public async Task OnlyInstallerFilesAreAccepted(string name)
    {
        var uploadId = Guid.NewGuid();

        await Assert.ThrowsAsync<GogUploadException>(() => SaveAsync(uploadId, name));

        Assert.Empty(_uploads.Files(uploadId));
    }

    [Fact]
    public async Task APathIsReducedToItsFileName()
    {
        var uploadId = Guid.NewGuid();

        var saved = await SaveAsync(uploadId, @"..\..\evil/setup_game.exe");

        Assert.Equal("setup_game.exe", saved.Name);
        Assert.True(File.Exists(Path.Combine(_uploads.DirectoryFor(uploadId), "setup_game.exe")));
    }

    [Fact]
    public async Task AFileCutShortLeavesNothingBehind()
    {
        var uploadId = Guid.NewGuid();

        await Assert.ThrowsAsync<IOException>(
            () => _uploads.SaveAsync(uploadId, "setup_game.exe", new FailingStream(), CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(_uploads.DirectoryFor(uploadId)));
    }

    [Fact]
    public async Task AnUploadMustHoldExactlyOneInstaller()
    {
        var uploadId = Guid.NewGuid();

        await SaveAsync(uploadId, "setup_game-1.bin");

        Assert.Contains("No installer", Assert.Throws<GogUploadException>(() => _uploads.FindInstaller(uploadId)).Message);

        await SaveAsync(uploadId, "setup_game.exe");
        await SaveAsync(uploadId, "patch_game.exe");

        Assert.Contains("Upload one at a time", Assert.Throws<GogUploadException>(() => _uploads.FindInstaller(uploadId)).Message);
    }

    [Fact]
    public async Task PruningSparesUploadsInUseAndRecentOnes()
    {
        var kept = Guid.NewGuid();
        var recent = Guid.NewGuid();
        var abandoned = Guid.NewGuid();

        foreach (var id in new[] { kept, recent, abandoned })
            await SaveAsync(id, "setup_game.exe");

        foreach (var id in new[] { kept, abandoned })
            File.SetLastWriteTimeUtc(Path.Combine(_uploads.DirectoryFor(id), "setup_game.exe"), DateTime.UtcNow.AddDays(-2));

        Assert.Equal(1, _uploads.Prune([kept], TimeSpan.FromDays(1)));

        Assert.True(Directory.Exists(_uploads.DirectoryFor(kept)));
        Assert.True(Directory.Exists(_uploads.DirectoryFor(recent)));
        Assert.False(Directory.Exists(_uploads.DirectoryFor(abandoned)));
    }

    private sealed class FailingStream() : MemoryStream(new byte[16])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Position == 0 ? base.ReadAsync(buffer[..8], cancellationToken) : throw new IOException("The connection was reset.");
    }
}

public class UploadEndpointTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly GogUploadStore _uploads;

    public UploadEndpointTests()
    {
        _uploads = new GogUploadStore(new GogSettingsStore(_directory.PluginPaths), NullLogger<GogUploadStore>.Instance);
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void TheEndpointsAreMappedUnderThePluginsPrefix()
    {
        var uploadId = Guid.NewGuid();

        Assert.Equal($"/api/Plugins/{new GogUploadEndpoints().RoutePrefix}/Uploads/{uploadId:N}", GogUploadEndpoints.UploadUrl(uploadId));
    }

    [Fact]
    public async Task AnUploadedFileIsStored()
    {
        var uploadId = Guid.NewGuid();

        var result = await PostAsync(uploadId, "setup_game.exe", [1, 2, 3, 4, 5]);

        Assert.IsType<NoContent>(result);
        Assert.Equal(new GogUploadedFile("setup_game.exe", 5), Assert.Single(_uploads.Files(uploadId)));
    }

    [Fact]
    public async Task AFileThatIsNotAnInstallerIsRefusedWithAReason()
    {
        var result = Assert.IsType<ContentHttpResult>(await PostAsync(Guid.NewGuid(), "notes.txt", [1]));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Contains("is not part of a GOG installer", result.ResponseContent);
    }

    [Fact]
    public async Task ARequestThatIsNotMultipartIsRefused()
    {
        var context = new DefaultHttpContext();

        context.Request.ContentType = "application/octet-stream";
        context.Request.Body = new MemoryStream([1, 2, 3]);

        var result = Assert.IsType<ContentHttpResult>(await GogUploadEndpoints.ReceiveAsync(
            Guid.NewGuid(), context.Request, _uploads, NullLogger<GogUploadStore>.Instance, CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
    }

    /// <summary>Posts a file the way the upload control does.</summary>
    private async Task<IResult> PostAsync(Guid uploadId, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);

        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);

        var body = new MemoryStream();

        await form.CopyToAsync(body);
        body.Position = 0;

        var context = new DefaultHttpContext();

        context.Request.ContentType = form.Headers.ContentType!.ToString();
        context.Request.Body = body;

        return await GogUploadEndpoints.ReceiveAsync(uploadId, context.Request, _uploads, NullLogger<GogUploadStore>.Instance, CancellationToken.None);
    }
}

public class UploadPageTests : BunitContext
{
    private readonly TempDirectory _directory = new();
    private readonly GogImportJobService _jobs;
    private readonly GogUploadStore _uploads;

    public UploadPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddLANCommanderServerUI();

        var settings = new GogSettingsStore(_directory.PluginPaths);

        _jobs = new GogImportJobService(_directory.PluginPaths, NullLogger<GogImportJobService>.Instance);
        _uploads = new GogUploadStore(settings, NullLogger<GogUploadStore>.Instance);

        Services.AddSingleton(_jobs);
        Services.AddSingleton(_uploads);
        Services.AddSingleton(settings);
        Services.AddSingleton<IStorageLocationProvider>(new FixedStorageLocations());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
            _directory.Dispose();
    }

    [Fact]
    public async Task AnUploadedInstallerIsQueuedForImport()
    {
        var page = Render<UploadPage>();

        Assert.Contains(FixedStorageLocations.Path, page.Markup);

        var uploadId = UploadId(page);

        await UploadAsync(page, uploadId, "setup_game.exe", "setup_game-1.bin");

        page.FindAll("button").Single(button => button.TextContent.Trim() == "Import").Click();

        var job = Assert.Single(_jobs.Jobs);

        Assert.Equal(uploadId, job.UploadId);
        Assert.Equal("setup_game", job.Title);
        Assert.Equal(FixedStorageLocations.Id, job.StorageLocationId);

        // The page is ready for the next one, and leaves the queued upload alone.
        page.Dispose();

        Assert.Equal(2, _uploads.Files(uploadId).Count);
    }

    [Fact]
    public async Task StartingOverDiscardsTheUpload()
    {
        var page = Render<UploadPage>();
        var uploadId = UploadId(page);

        await UploadAsync(page, uploadId, "setup_game.exe");

        page.FindAll("button").Single(button => button.TextContent.Trim() == "Start over").Click();

        Assert.Empty(_uploads.Files(uploadId));
        Assert.Empty(_jobs.Jobs);
    }

    /// <summary>The upload the page's upload control is posting to.</summary>
    private static Guid UploadId(IRenderedComponent<UploadPage> page)
        => Guid.ParseExact(page.FindComponent<Upload>().Instance.Action.Split('/').Last(), "N");

    /// <summary>
    /// Puts files in place as the upload control's posts would, then tells the page as the control
    /// does.
    /// </summary>
    private async Task UploadAsync(IRenderedComponent<UploadPage> page, Guid uploadId, params string[] names)
    {
        foreach (var name in names)
            await _uploads.SaveAsync(uploadId, name, new MemoryStream([1]), CancellationToken.None);

        var upload = page.FindComponent<Upload>().Instance;

        await page.InvokeAsync(() => upload.OnUploaded.InvokeAsync(new UploadedFile("upload-0", names[^1], 1)));
    }

    private sealed class FixedStorageLocations : IStorageLocationProvider
    {
        public static readonly Guid Id = Guid.NewGuid();
        public const string Path = @"D:\Archives";

        public Task<IReadOnlyList<StorageLocationInfo>> GetAsync(StorageLocationType type, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StorageLocationInfo>>([new StorageLocationInfo(Id, Path, type, Default: true)]);
    }
}
