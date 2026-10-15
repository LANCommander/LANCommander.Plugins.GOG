using Microsoft.AspNetCore.DataProtection;

namespace LANCommander.Plugins.GOG.Storage;

/// <summary>A linked GOG account, as the UI sees it. Carries no tokens.</summary>
public sealed record GogAccount(
    Guid Id,
    string UserId,
    string Username,
    string? AvatarUrl,
    DateTimeOffset LinkedOn,
    bool NeedsReauthentication,
    string? LastError);

/// <summary>The tokens GOG issued for an account.</summary>
public sealed record GogTokenSet(string AccessToken, string RefreshToken, DateTimeOffset ExpiresOn);

/// <summary>An account as it is written to disk, with its tokens encrypted.</summary>
public sealed record StoredGogAccount
{
    public Guid Id { get; init; }
    public string UserId { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string? AvatarUrl { get; init; }
    public DateTimeOffset LinkedOn { get; init; }
    public bool NeedsReauthentication { get; init; }
    public string? LastError { get; init; }
    public string ProtectedAccessToken { get; init; } = string.Empty;
    public string ProtectedRefreshToken { get; init; } = string.Empty;
    public DateTimeOffset AccessTokenExpiresOn { get; init; }
}

/// <summary>
/// Keeps the linked GOG accounts and their tokens.
/// </summary>
/// <remarks>
/// Tokens are encrypted with a key ring that belongs to the plugin and lives beside its state, so
/// they survive restarts and container rebuilds for as long as the data directory does, without
/// depending on how the host has configured its own data protection.
/// </remarks>
public sealed class GogAccountStore
{
    private const string Purpose = "LANCommander.Plugins.GOG.Tokens";

    private readonly GogPluginPaths _paths;
    private readonly IDataProtector _protector;
    private readonly Lock _lock = new();
    private List<StoredGogAccount>? _accounts;

    public GogAccountStore(GogPluginPaths paths)
        : this(paths, DataProtectionProvider.Create(new DirectoryInfo(paths.Keys)))
    {
    }

    public GogAccountStore(GogPluginPaths paths, IDataProtectionProvider protection)
    {
        _paths = paths;
        _protector = protection.CreateProtector(Purpose);
    }

    /// <summary>Raised after any account is added, changed or removed.</summary>
    public event Action? Changed;

    public IReadOnlyList<GogAccount> GetAll()
    {
        lock (_lock)
            return Accounts.Select(ToAccount).OrderBy(account => account.Username, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public GogAccount? Get(Guid id)
    {
        lock (_lock)
            return Accounts.FirstOrDefault(account => account.Id == id) is { } stored ? ToAccount(stored) : null;
    }

    /// <summary>
    /// Links an account, or refreshes the tokens of one that is already linked.
    /// </summary>
    public GogAccount Link(string userId, string username, string? avatarUrl, GogTokenSet tokens)
    {
        GogAccount linked;

        lock (_lock)
        {
            var existing = Accounts.FirstOrDefault(account => account.UserId == userId);

            var stored = Protect(new StoredGogAccount
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                UserId = userId,
                Username = username,
                AvatarUrl = avatarUrl,
                LinkedOn = existing?.LinkedOn ?? DateTimeOffset.UtcNow,
            }, tokens);

            Replace(existing, stored);

            linked = ToAccount(stored);
        }

        Changed?.Invoke();

        return linked;
    }

    /// <summary>Reads an account's tokens, or <see langword="null"/> when it is not linked.</summary>
    public GogTokenSet? GetTokens(Guid id)
    {
        lock (_lock)
        {
            var stored = Accounts.FirstOrDefault(account => account.Id == id);

            if (stored is null || stored.ProtectedRefreshToken.Length == 0)
                return null;

            try
            {
                return new GogTokenSet(
                    stored.ProtectedAccessToken.Length == 0 ? string.Empty : _protector.Unprotect(stored.ProtectedAccessToken),
                    _protector.Unprotect(stored.ProtectedRefreshToken),
                    stored.AccessTokenExpiresOn);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // The key ring was lost or replaced; the account has to be linked again.
                return null;
            }
        }
    }

    /// <summary>Stores tokens GOG issued on a refresh, clearing any earlier failure.</summary>
    public void UpdateTokens(Guid id, GogTokenSet tokens)
    {
        lock (_lock)
        {
            var existing = Accounts.FirstOrDefault(account => account.Id == id);

            if (existing is null)
                return;

            Replace(existing, Protect(existing with { NeedsReauthentication = false, LastError = null }, tokens));
        }

        Changed?.Invoke();
    }

    /// <summary>Records that GOG no longer accepts an account's tokens.</summary>
    public void MarkNeedsReauthentication(Guid id, string reason)
    {
        lock (_lock)
        {
            var existing = Accounts.FirstOrDefault(account => account.Id == id);

            if (existing is null)
                return;

            Replace(existing, existing with { NeedsReauthentication = true, LastError = reason });
        }

        Changed?.Invoke();
    }

    public bool Remove(Guid id)
    {
        lock (_lock)
        {
            if (Accounts.RemoveAll(account => account.Id == id) == 0)
                return false;

            Save();
        }

        Changed?.Invoke();

        return true;
    }

    private List<StoredGogAccount> Accounts
        => _accounts ??= JsonFile.Read(_paths.Accounts, GogJsonContext.Default.ListStoredGogAccount) ?? [];

    private StoredGogAccount Protect(StoredGogAccount account, GogTokenSet tokens) => account with
    {
        ProtectedAccessToken = tokens.AccessToken.Length == 0 ? string.Empty : _protector.Protect(tokens.AccessToken),
        ProtectedRefreshToken = _protector.Protect(tokens.RefreshToken),
        AccessTokenExpiresOn = tokens.ExpiresOn,
    };

    /// <remarks>Called with the lock held. Callers raise <see cref="Changed"/> once they have released it.</remarks>
    private void Replace(StoredGogAccount? existing, StoredGogAccount replacement)
    {
        if (existing is not null)
            Accounts[Accounts.IndexOf(existing)] = replacement;
        else
            Accounts.Add(replacement);

        Save();
    }

    private void Save() => JsonFile.Write(_paths.Accounts, Accounts, GogJsonContext.Default.ListStoredGogAccount);

    private static GogAccount ToAccount(StoredGogAccount stored) => new(
        stored.Id,
        stored.UserId,
        stored.Username,
        stored.AvatarUrl,
        stored.LinkedOn,
        stored.NeedsReauthentication,
        stored.LastError);
}
