using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LANCommander.Plugins.GOG.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.Plugins.GOG.Tests;

public class DownloaderTests : IDisposable
{
    private const string FileUrl = "https://cdn.example/token=abc/secure/setup_game_1.0_(123).exe?sig=1";
    private const string ChecksumUrl = "https://cdn.example/setup_game_1.0_(123).exe.xml";

    private readonly TempDirectory _directory = new();
    private readonly byte[] _content = RandomNumberGenerator.GetBytes(300_000);

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void TheFileIsNamedAsTheCdnNamesIt()
        => Assert.Equal("setup_game_1.0_(123).exe", GogDownloader.FileName(FileUrl));

    [Fact]
    public async Task AFileIsDownloadedAndChecked()
    {
        var handler = Serve(_content, Md5(_content));

        var path = await Downloader(handler).DownloadAsync(Resolve, _directory.Path, _content.Length, verify: true, progress: null, CancellationToken.None);

        Assert.Equal(Path.Combine(_directory.Path, "setup_game_1.0_(123).exe"), path);
        Assert.Equal(_content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task APartialDownloadResumesFromWhereItStopped()
    {
        var partial = Path.Combine(_directory.Path, "setup_game_1.0_(123).exe.part");

        await File.WriteAllBytesAsync(partial, _content[..100_000]);

        var handler = Serve(_content, md5: null);

        var path = await Downloader(handler).DownloadAsync(Resolve, _directory.Path, _content.Length, verify: false, progress: null, CancellationToken.None);

        Assert.Equal(_content, await File.ReadAllBytesAsync(path));

        var range = handler.Requests.Single(request => request.RequestUri!.AbsoluteUri == FileUrl).Headers.Range;

        Assert.Equal(100_000, range!.Ranges.Single().From);
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task AFileThatNeverMatchesItsChecksumFails()
    {
        var handler = Serve(_content, md5: new string('0', 32));

        await Assert.ThrowsAsync<GogDownloadVerificationException>(() =>
            Downloader(handler).DownloadAsync(Resolve, _directory.Path, _content.Length, verify: true, progress: null, CancellationToken.None));

        // A bad file is not left behind to be mistaken for a finished one.
        Assert.Empty(Directory.GetFiles(_directory.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASizeTheApiRoundedToAWholeMebibyteIsAccepted(bool publishesChecksum)
    {
        // GOG's products API lists a 788 KB installer as 1,048,576 bytes.
        var handler = Serve(_content, publishesChecksum ? Md5(_content) : null);

        var path = await Downloader(handler).DownloadAsync(Resolve, _directory.Path, 1 << 20, verify: true, progress: null, CancellationToken.None);

        Assert.Equal(_content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AFileThatNeverMatchesTheChecksumSizeFails()
    {
        var handler = Serve(_content, Md5(_content), totalSize: _content.Length + 1);

        await Assert.ThrowsAsync<GogDownloadVerificationException>(() =>
            Downloader(handler).DownloadAsync(Resolve, _directory.Path, 1 << 20, verify: false, progress: null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_directory.Path));
    }

    [Fact]
    public async Task AFileThatNeverMatchesAnExactListedSizeFails()
    {
        var handler = Serve(_content, md5: null);

        await Assert.ThrowsAsync<GogDownloadVerificationException>(() =>
            Downloader(handler).DownloadAsync(Resolve, _directory.Path, _content.Length + 1, verify: false, progress: null, CancellationToken.None));
    }

    [Fact]
    public async Task ATransientFailureIsRetried()
    {
        var failures = 1;
        var inner = Serve(_content, md5: null);
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri == FileUrl && failures-- > 0)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            return inner.Respond(request);
        });

        var path = await Downloader(handler).DownloadAsync(Resolve, _directory.Path, _content.Length, verify: false, progress: null, CancellationToken.None);

        Assert.Equal(_content, await File.ReadAllBytesAsync(path));
    }

    private static Task<GogDownlink> Resolve(CancellationToken _)
        => Task.FromResult(new GogDownlink { Url = FileUrl, ChecksumUrl = ChecksumUrl });

    private static GogDownloader Downloader(HttpMessageHandler handler)
        => new(new HttpClient(handler), NullLogger<GogDownloader>.Instance) { RetryDelay = _ => TimeSpan.Zero };

    private static string Md5(byte[] content) => Convert.ToHexStringLower(MD5.HashData(content));

    /// <summary>Serves the file, honouring ranges, and its checksum.</summary>
    private static ServingHandler Serve(byte[] content, string? md5, long? totalSize = null) => new(content, md5, totalSize ?? content.Length);

    private sealed class ServingHandler(byte[] content, string? md5, long totalSize) : FakeHttpHandlerBase
    {
        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            if (request.RequestUri!.AbsoluteUri == ChecksumUrl)
                return md5 is null
                    ? FakeHttpHandler.NotFound()
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"""<file name="setup.exe" available="1" md5="{md5}" chunks="1" total_size="{totalSize}"></file>"""),
                    };

            var from = (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content[from..]),
            };

            if (from > 0)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, content.Length - 1, content.Length);

            return response;
        }

        protected override HttpResponseMessage Handle(HttpRequestMessage request) => Respond(request);
    }
}

/// <summary>A handler that records requests and answers from an override.</summary>
public abstract class FakeHttpHandlerBase : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected abstract HttpResponseMessage Handle(HttpRequestMessage request);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
            Requests.Add(request);

        return Task.FromResult(Handle(request));
    }
}
