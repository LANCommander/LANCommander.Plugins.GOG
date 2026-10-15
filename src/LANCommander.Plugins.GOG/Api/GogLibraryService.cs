using System.Collections.Concurrent;
using LANCommander.Plugins.GOG.Storage;

namespace LANCommander.Plugins.GOG.Api;

/// <summary>
/// Keeps each account's library for a few minutes, since reading a large one takes a request per
/// hundred games.
/// </summary>
public sealed class GogLibraryCache(TimeProvider? time = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset ReadOn, IReadOnlyList<GogOwnedProduct> Products)> _libraries = new();

    public bool TryGet(Guid accountId, out IReadOnlyList<GogOwnedProduct> products)
    {
        if (_libraries.TryGetValue(accountId, out var cached) && _time.GetUtcNow() - cached.ReadOn < Lifetime)
        {
            products = cached.Products;

            return true;
        }

        products = [];

        return false;
    }

    public void Set(Guid accountId, IReadOnlyList<GogOwnedProduct> products)
        => _libraries[accountId] = (_time.GetUtcNow(), products);

    public void Invalidate(Guid accountId) => _libraries.TryRemove(accountId, out _);
}

/// <summary>Reads the games linked accounts own.</summary>
public sealed class GogLibraryService(GogApiClient api, GogAccountStore accounts, GogLibraryCache cache)
{
    /// <summary>
    /// Reads one account's games, or every linked account's when <paramref name="accountId"/> is
    /// null. A game owned on more than one account is listed once, against the first account
    /// that can still sign in.
    /// </summary>
    /// <returns>The games, and a note for each account that could not be read.</returns>
    public async Task<(IReadOnlyList<GogOwnedProduct> Games, IReadOnlyList<string> Problems)> GetOwnedGamesAsync(
        Guid? accountId,
        bool refresh,
        CancellationToken cancellationToken = default)
    {
        var targets = accounts.GetAll()
            .Where(account => accountId is null || account.Id == accountId)
            .ToList();

        var games = new List<GogOwnedProduct>();
        var seen = new HashSet<string>();
        var problems = new List<string>();

        foreach (var account in targets)
        {
            if (account.NeedsReauthentication)
            {
                problems.Add($"{account.Username} has to be signed in again.");

                continue;
            }

            if (refresh)
                cache.Invalidate(account.Id);

            if (!cache.TryGet(account.Id, out var owned))
            {
                try
                {
                    owned = await api.GetOwnedGamesAsync(account.Id, cancellationToken);

                    cache.Set(account.Id, owned);
                }
                catch (Exception exception) when (exception is GogAuthenticationException or GogApiException or HttpRequestException)
                {
                    problems.Add($"Could not read the library of {account.Username}: {exception.Message}");

                    continue;
                }
            }

            games.AddRange(owned.Where(game => seen.Add(game.ProductId)));
        }

        return (games.OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase).ToList(), problems);
    }
}

/// <summary>Links a GOG account from what an administrator pastes back after signing in.</summary>
public sealed class GogAccountLinker(GogAuthClient auth, GogApiClient api, GogAccountStore accounts)
{
    /// <exception cref="ArgumentException">Nothing that looks like a code was pasted.</exception>
    /// <exception cref="GogAuthenticationException">GOG refused the code.</exception>
    public async Task<GogAccount> LinkAsync(string pasted, CancellationToken cancellationToken = default)
    {
        var code = GogAuthClient.ParseCode(pasted)
                   ?? throw new ArgumentException(
                       "Paste the whole address GOG shows after you sign in. It contains \"code=\".");

        var tokens = await auth.ExchangeCodeAsync(code, cancellationToken);
        var user = await api.GetUserDataAsync(tokens.AccessToken, cancellationToken);

        return accounts.Link(user.UserId!, user.Username ?? user.UserId!, user.Avatar, tokens);
    }
}
