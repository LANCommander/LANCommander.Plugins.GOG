using System.Net;
using System.Text;
using LANCommander.Plugins.GOG.Api;
using LANCommander.Plugins.GOG.Storage;
using LANCommander.Server.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.Plugins.GOG.Tests;

/// <summary>A scratch directory, deleted afterwards.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lancommander-gog-plugin-test-{Guid.NewGuid():N}");

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public GogPluginPaths PluginPaths => new(System.IO.Path.Combine(Path, "state"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A plugin load context cannot be unloaded, so the assemblies it loaded stay locked
            // until the test process exits.
        }
    }
}

/// <summary>Answers HTTP requests from a function, recording each one.</summary>
public sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
            Requests.Add(request);

        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);
}

/// <summary>Builds the plugin's services around fakes.</summary>
public static class TestServices
{
    /// <summary>Protection that does nothing, so tests need no key ring.</summary>
    public static IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();

    public static GogAccountStore Accounts(GogPluginPaths paths) => new(paths, Protection);

    public static GogAuthClient Auth(FakeHttpHandler handler) => new(new HttpClient(handler));

    public static GogSessionManager Sessions(GogAccountStore accounts, FakeHttpHandler handler)
        => new(accounts, Auth(handler), NullLogger<GogSessionManager>.Instance);

    public static GogApiClient Api(GogAccountStore accounts, FakeHttpHandler handler)
        => new(new HttpClient(handler), Sessions(accounts, handler));

    /// <summary>Links an account whose access token is good for an hour.</summary>
    public static GogAccount LinkedAccount(GogAccountStore accounts, string accessToken = "access", string refreshToken = "refresh")
        => accounts.Link("1001", "tester", null, new GogTokenSet(accessToken, refreshToken, DateTimeOffset.UtcNow.AddHours(1)));
}

/// <summary>
/// Locates the real installers the integration tests run against.
/// </summary>
/// <remarks>
/// Installers are gigabytes each and cannot be committed, so these tests are skipped unless
/// <c>GOG_SAMPLE_DIR</c> points at a directory holding them.
/// </remarks>
public static class SampleInstallers
{
    public const string DirectoryVariable = "GOG_SAMPLE_DIR";

    public const string CloseCombat2 = "setup_close_combat_2_2.0b_(17798).exe";

    public static string? Find(string fileName)
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return null;

        var path = System.IO.Path.Combine(directory, fileName);

        return File.Exists(path) ? path : null;
    }
}

/// <summary>Marks a test that needs a real installer, skipping it when none is configured.</summary>
public sealed class InstallerFactAttribute : FactAttribute
{
    public InstallerFactAttribute(string fileName)
    {
        if (SampleInstallers.Find(fileName) is null)
            Skip = $"Set {SampleInstallers.DirectoryVariable} to a directory holding \"{fileName}\" to run this test.";
    }
}

/// <summary>A server with a fixed set of redistributables, which the plugin only reads.</summary>
public sealed class FixedRedistributables(IReadOnlyList<RedistributableInfo> redistributables) : IRedistributableProvider
{
    public Task<IReadOnlyList<RedistributableInfo>> GetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(redistributables);

    public Task<RedistributableInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(redistributables.FirstOrDefault(redistributable => redistributable.Id == id));

    public Task<RedistributableInfo?> FindAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(redistributables.FirstOrDefault(redistributable => string.Equals(redistributable.Name, name, StringComparison.OrdinalIgnoreCase)));

    public Task<RedistributableInfo> AddAsync(RedistributableInfo redistributable, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RedistributableInfo> UpdateAsync(RedistributableInfo redistributable, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
