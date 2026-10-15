using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LANCommander.Plugins.GOG.Storage;

namespace LANCommander.Plugins.GOG.Api;

/// <summary>Raised when GOG refuses to issue tokens.</summary>
public sealed class GogAuthenticationException(string message, bool tokenRevoked = false) : Exception(message)
{
    /// <summary>True when GOG rejected the token itself, so the account must sign in again.</summary>
    public bool TokenRevoked { get; } = tokenRevoked;
}

/// <summary>
/// Signs in to GOG the way GOG Galaxy does.
/// </summary>
/// <remarks>
/// GOG offers no OAuth registration to third parties. Every tool that reads a GOG library
/// (Heroic, lgogdownloader, minigalaxy) signs in as the Galaxy client: the user signs in on GOG's
/// own page, GOG redirects to <c>embed.gog.com/on_login_success?code=...</c>, and the code is
/// exchanged for tokens. A server cannot receive that redirect, so the administrator copies the
/// address it lands on and pastes it back.
/// </remarks>
public sealed class GogAuthClient(HttpClient http)
{
    public const string ClientId = "46899977096215655";

    /// <summary>Galaxy's client secret. It ships in every copy of the Galaxy client.</summary>
    public const string ClientSecret = "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";

    public const string RedirectUri = "https://embed.gog.com/on_login_success?origin=client";

    private const string TokenEndpoint = "https://auth.gog.com/token";

    /// <summary>The page an administrator signs in on.</summary>
    public static string LoginUrl { get; } =
        "https://auth.gog.com/auth" +
        $"?client_id={ClientId}" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        "&response_type=code" +
        "&layout=client2";

    /// <summary>
    /// Pulls the authorization code out of what was pasted: the address GOG redirected to, or the
    /// bare code.
    /// </summary>
    /// <returns>The code, or <see langword="null"/> when there is none to be found.</returns>
    public static string? ParseCode(string? pasted)
    {
        var text = pasted?.Trim();

        if (string.IsNullOrEmpty(text))
            return null;

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');

                if (separator > 0 && pair[..separator] == "code")
                {
                    var code = Uri.UnescapeDataString(pair[(separator + 1)..]);

                    return code.Length > 0 ? code : null;
                }
            }

            return null;
        }

        // A bare code is URL-safe text with no spaces.
        return text.Any(char.IsWhiteSpace) || text.Contains('?') || text.Contains('/') ? null : text;
    }

    /// <summary>Exchanges an authorization code for tokens.</summary>
    public Task<GogTokenSet> ExchangeCodeAsync(string code, CancellationToken cancellationToken = default)
        => RequestAsync(
            $"grant_type=authorization_code&code={Uri.EscapeDataString(code)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}",
            cancellationToken);

    /// <summary>Exchanges a refresh token for fresh tokens.</summary>
    public Task<GogTokenSet> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
        => RequestAsync(
            $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refreshToken)}",
            cancellationToken);

    private async Task<GogTokenSet> RequestAsync(string grant, CancellationToken cancellationToken)
    {
        var url = $"{TokenEndpoint}?client_id={ClientId}&client_secret={ClientSecret}&{grant}";

        using var response = await http.GetAsync(url, cancellationToken);

        GogTokenResponse? body = null;

        try
        {
            body = await response.Content.ReadFromJsonAsync(GogJsonContext.Default.GogTokenResponse, cancellationToken);
        }
        catch (JsonException)
        {
        }

        if (!response.IsSuccessStatusCode || body is null || string.IsNullOrEmpty(body.AccessToken))
        {
            var reason = body?.ErrorDescription ?? body?.Error
                         ?? $"GOG answered {(int)response.StatusCode} {response.ReasonPhrase}";

            // invalid_grant is what GOG answers for a used, expired or revoked code or refresh token.
            var revoked = body?.Error is "invalid_grant" or "invalid_token"
                          || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest;

            throw new GogAuthenticationException($"GOG would not sign in: {reason}", revoked);
        }

        var expiresIn = body.ExpiresIn > 0 ? body.ExpiresIn : 3600;

        return new GogTokenSet(body.AccessToken, body.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }
}
