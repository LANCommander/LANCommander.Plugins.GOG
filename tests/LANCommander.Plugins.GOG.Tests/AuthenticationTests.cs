using System.Net;
using LANCommander.Plugins.GOG.Api;
using LANCommander.Plugins.GOG.Storage;

namespace LANCommander.Plugins.GOG.Tests;

public class AuthenticationTests
{
    [Fact]
    public void TheLoginPageRedirectsToTheGalaxySuccessPage()
    {
        var uri = new Uri(GogAuthClient.LoginUrl);

        Assert.Equal("auth.gog.com", uri.Host);
        Assert.Contains($"client_id={GogAuthClient.ClientId}", uri.Query);
        Assert.Contains("response_type=code", uri.Query);
        Assert.Contains(Uri.EscapeDataString(GogAuthClient.RedirectUri), uri.Query);
    }

    [Theory]
    [InlineData("https://embed.gog.com/on_login_success?origin=client&code=abc-DEF_123", "abc-DEF_123")]
    [InlineData("  https://embed.gog.com/on_login_success?code=abc%2Bdef&origin=client  ", "abc+def")]
    [InlineData("abc-DEF_123", "abc-DEF_123")]
    public void TheCodeIsFoundInWhatIsPasted(string pasted, string expected)
        => Assert.Equal(expected, GogAuthClient.ParseCode(pasted));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://embed.gog.com/on_login_success?origin=client")]
    [InlineData("https://embed.gog.com/on_login_success?origin=client&code=")]
    [InlineData("not a code")]
    public void NothingIsFoundWhenThereIsNoCode(string pasted)
        => Assert.Null(GogAuthClient.ParseCode(pasted));

    [Fact]
    public async Task ACodeIsExchangedForTokens()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"access_token":"a1","refresh_token":"r1","expires_in":3600,"user_id":"1001"}"""));

        var tokens = await TestServices.Auth(handler).ExchangeCodeAsync("the-code");

        Assert.Equal("a1", tokens.AccessToken);
        Assert.Equal("r1", tokens.RefreshToken);
        Assert.InRange(tokens.ExpiresOn, DateTimeOffset.UtcNow.AddMinutes(59), DateTimeOffset.UtcNow.AddMinutes(61));

        var query = handler.Requests.Single().RequestUri!.Query;

        Assert.Contains("grant_type=authorization_code", query);
        Assert.Contains("code=the-code", query);
    }

    [Fact]
    public async Task ARejectedCodeSaysTheTokenWasRevoked()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"error":"invalid_grant","error_description":"The authorization code has expired"}""", HttpStatusCode.BadRequest));

        var exception = await Assert.ThrowsAsync<GogAuthenticationException>(
            () => TestServices.Auth(handler).ExchangeCodeAsync("old-code"));

        Assert.True(exception.TokenRevoked);
        Assert.Contains("has expired", exception.Message);
    }
}

public class SessionTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task AFreshTokenIsUsedAsItIs()
    {
        var accounts = TestServices.Accounts(_directory.PluginPaths);
        var account = TestServices.LinkedAccount(accounts);
        var handler = new FakeHttpHandler(_ => throw new InvalidOperationException("No request expected"));

        var token = await TestServices.Sessions(accounts, handler).GetAccessTokenAsync(account.Id);

        Assert.Equal("access", token);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnExpiringTokenIsRefreshedAndTheNewTokensAreKept()
    {
        var accounts = TestServices.Accounts(_directory.PluginPaths);
        var account = accounts.Link("1001", "tester", null, new GogTokenSet("old", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(2)));
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"access_token":"new","refresh_token":"refresh-2","expires_in":3600}"""));

        var token = await TestServices.Sessions(accounts, handler).GetAccessTokenAsync(account.Id);

        Assert.Equal("new", token);
        Assert.Contains("refresh_token=refresh-1", handler.Requests.Single().RequestUri!.Query);

        // A second store reading the same file sees the rotated token, so it survives a restart.
        var reloaded = TestServices.Accounts(_directory.PluginPaths).GetTokens(account.Id);

        Assert.Equal("refresh-2", reloaded!.RefreshToken);
    }

    [Fact]
    public async Task ARevokedRefreshTokenMarksTheAccountForSigningInAgain()
    {
        var accounts = TestServices.Accounts(_directory.PluginPaths);
        var account = accounts.Link("1001", "tester", null, new GogTokenSet("old", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));

        await Assert.ThrowsAsync<GogAuthenticationException>(
            () => TestServices.Sessions(accounts, handler).GetAccessTokenAsync(account.Id));

        Assert.True(accounts.Get(account.Id)!.NeedsReauthentication);

        // Once marked, the account is not refreshed again until it is linked again.
        await Assert.ThrowsAsync<GogAuthenticationException>(
            () => TestServices.Sessions(accounts, handler).GetAccessTokenAsync(account.Id));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneRefresh()
    {
        var accounts = TestServices.Accounts(_directory.PluginPaths);
        var account = accounts.Link("1001", "tester", null, new GogTokenSet("old", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"access_token":"new","refresh_token":"refresh-2","expires_in":3600}"""));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => TestServices.Sessions(accounts, handler).GetAccessTokenAsync(account.Id)));

        Assert.All(tokens, token => Assert.Equal("new", token));
        Assert.Single(handler.Requests);
    }
}

public class AccountStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void TokensAreNotWrittenInTheClear()
    {
        var accounts = TestServices.Accounts(_directory.PluginPaths);

        TestServices.LinkedAccount(accounts, accessToken: "secret-access-token", refreshToken: "secret-refresh-token");

        var json = File.ReadAllText(_directory.PluginPaths.Accounts);

        Assert.DoesNotContain("secret-access-token", json);
        Assert.DoesNotContain("secret-refresh-token", json);
        Assert.Contains("tester", json);
    }

    [Fact]
    public void LinkingTheSameUserAgainUpdatesTheAccount()
    {
        var accounts = TestServices.Accounts(_directory.PluginPaths);

        var first = accounts.Link("1001", "tester", null, new GogTokenSet("a", "r1", DateTimeOffset.UtcNow.AddHours(1)));
        accounts.MarkNeedsReauthentication(first.Id, "expired");

        var second = accounts.Link("1001", "tester-renamed", null, new GogTokenSet("b", "r2", DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Equal(first.Id, second.Id);
        Assert.False(second.NeedsReauthentication);
        Assert.Equal("tester-renamed", Assert.Single(accounts.GetAll()).Username);
        Assert.Equal("r2", accounts.GetTokens(first.Id)!.RefreshToken);
    }

    [Fact]
    public void TokensThatCannotBeDecryptedAreTreatedAsMissing()
    {
        var account = TestServices.LinkedAccount(TestServices.Accounts(_directory.PluginPaths));

        // A store with a different key ring stands in for a lost or replaced one.
        var other = new GogAccountStore(_directory.PluginPaths, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());

        Assert.Null(other.GetTokens(account.Id));
    }
}
