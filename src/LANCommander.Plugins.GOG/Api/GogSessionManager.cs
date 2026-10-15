using System.Collections.Concurrent;
using LANCommander.Plugins.GOG.Storage;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.GOG.Api;

/// <summary>
/// Hands out access tokens that are good to use, refreshing them when they are about to expire.
/// </summary>
/// <remarks>
/// Refreshes for one account are serialized: GOG issues a new refresh token on every refresh, and
/// two refreshes racing on the same old token would leave one of them holding a token that may no
/// longer be honoured.
/// </remarks>
public sealed class GogSessionManager(GogAccountStore accounts, GogAuthClient auth, ILogger<GogSessionManager> logger)
{
    /// <summary>How long before expiry a token is treated as expired.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    // Static, because the manager itself is created per use alongside the HTTP clients it uses.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>Returns a usable access token for an account.</summary>
    /// <exception cref="GogAuthenticationException">The account has to sign in again.</exception>
    public async Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);

        try
        {
            var account = accounts.Get(accountId)
                          ?? throw new GogAuthenticationException("That GOG account is no longer linked.");

            if (account.NeedsReauthentication)
                throw new GogAuthenticationException(
                    $"GOG account {account.Username} has to be signed in again.", tokenRevoked: true);

            var tokens = accounts.GetTokens(accountId);

            if (tokens is null)
            {
                accounts.MarkNeedsReauthentication(accountId, "The stored tokens could not be read.");

                throw new GogAuthenticationException(
                    $"GOG account {account.Username} has to be signed in again.", tokenRevoked: true);
            }

            if (tokens.AccessToken.Length > 0 && tokens.ExpiresOn - Margin > DateTimeOffset.UtcNow)
                return tokens.AccessToken;

            try
            {
                var refreshed = await auth.RefreshAsync(tokens.RefreshToken, cancellationToken);

                // Keep the old refresh token if GOG did not issue a new one.
                if (string.IsNullOrEmpty(refreshed.RefreshToken))
                    refreshed = refreshed with { RefreshToken = tokens.RefreshToken };

                accounts.UpdateTokens(accountId, refreshed);

                return refreshed.AccessToken;
            }
            catch (GogAuthenticationException exception) when (exception.TokenRevoked)
            {
                logger.LogWarning(
                    "GOG rejected the refresh token for {Username}: {Message}", account.Username, exception.Message);

                accounts.MarkNeedsReauthentication(accountId, exception.Message);

                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
