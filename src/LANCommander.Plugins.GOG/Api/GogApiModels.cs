using System.Text.Json.Serialization;

namespace LANCommander.Plugins.GOG.Api;

/// <summary>What <c>auth.gog.com/token</c> returns.</summary>
public sealed record GogTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = string.Empty;
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; init; } = string.Empty;
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("user_id")] public string? UserId { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; init; }
}

/// <summary>What <c>embed.gog.com/userData.json</c> returns, in part.</summary>
public sealed record GogUserData
{
    [JsonPropertyName("isLoggedIn")] public bool IsLoggedIn { get; init; }
    [JsonPropertyName("userId")] public string? UserId { get; init; }
    [JsonPropertyName("username")] public string? Username { get; init; }
    [JsonPropertyName("avatar")] public string? Avatar { get; init; }
}

/// <summary>One page of <c>embed.gog.com/account/getFilteredProducts</c>.</summary>
public sealed record GogFilteredProductsPage
{
    [JsonPropertyName("page")] public int Page { get; init; }
    [JsonPropertyName("totalPages")] public int TotalPages { get; init; }
    [JsonPropertyName("totalProducts")] public int TotalProducts { get; init; }
    [JsonPropertyName("products")] public List<GogOwnedProductDto> Products { get; init; } = [];
}

public sealed record GogOwnedProductDto
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("image")] public string? Image { get; init; }
    [JsonPropertyName("slug")] public string? Slug { get; init; }
    [JsonPropertyName("isGame")] public bool IsGame { get; init; } = true;
    [JsonPropertyName("worksOn")] public GogWorksOn? WorksOn { get; init; }
}

public sealed record GogWorksOn
{
    [JsonPropertyName("Windows")] public bool Windows { get; init; }
    [JsonPropertyName("Mac")] public bool Mac { get; init; }
    [JsonPropertyName("Linux")] public bool Linux { get; init; }
}

/// <summary><c>api.gog.com/products/{id}?expand=downloads</c>, in part.</summary>
public sealed record GogProductDetails
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("downloads")] public GogProductDownloads? Downloads { get; init; }
}

public sealed record GogProductDownloads
{
    [JsonPropertyName("installers")] public List<GogInstallerDownload> Installers { get; init; } = [];
}

public sealed record GogInstallerDownload
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("os")] public string? Os { get; init; }
    [JsonPropertyName("language")] public string? Language { get; init; }
    [JsonPropertyName("language_full")] public string? LanguageFull { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("total_size")] public long TotalSize { get; init; }
    [JsonPropertyName("files")] public List<GogInstallerFile> Files { get; init; } = [];
}

public sealed record GogInstallerFile
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("downlink")] public string? Downlink { get; init; }
}

/// <summary>What following an authenticated downlink returns.</summary>
public sealed record GogDownlink
{
    [JsonPropertyName("downlink")] public string? Url { get; init; }
    [JsonPropertyName("checksum")] public string? ChecksumUrl { get; init; }
}

/// <summary>A game an account owns, ready for display.</summary>
public sealed record GogOwnedProduct(
    string ProductId,
    string Title,
    string? ImageUrl,
    bool Windows,
    bool Mac,
    bool Linux,
    Guid AccountId);
