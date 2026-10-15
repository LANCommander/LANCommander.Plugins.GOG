using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

namespace LANCommander.Plugins.GOG.Api;

/// <summary>Raised when a GOG API call fails for a reason other than authentication.</summary>
public sealed class GogApiException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>
/// Reads an account's library and the downloads its games offer.
/// </summary>
public sealed class GogApiClient(HttpClient http, GogSessionManager sessions)
{
    private const string UserDataUrl = "https://embed.gog.com/userData.json";
    private const string FilteredProductsUrl = "https://embed.gog.com/account/getFilteredProducts";
    private const string ProductUrl = "https://api.gog.com/products/";

    /// <summary>Reads who an access token belongs to. Used while linking, before the account is stored.</summary>
    public async Task<GogUserData> GetUserDataAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var user = await GetAsync(UserDataUrl, accessToken, GogJsonContext.Default.GogUserData, cancellationToken);

        if (!user.IsLoggedIn || string.IsNullOrEmpty(user.UserId))
            throw new GogAuthenticationException("GOG did not recognise the sign-in.", tokenRevoked: true);

        return user;
    }

    /// <summary>Reads every game an account owns.</summary>
    public async Task<IReadOnlyList<GogOwnedProduct>> GetOwnedGamesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var products = new List<GogOwnedProduct>();

        for (var page = 1; ; page++)
        {
            var token = await sessions.GetAccessTokenAsync(accountId, cancellationToken);

            var result = await GetAsync(
                $"{FilteredProductsUrl}?mediaType=1&sortBy=title&page={page}",
                token,
                GogJsonContext.Default.GogFilteredProductsPage,
                cancellationToken);

            products.AddRange(result.Products
                .Where(product => product.IsGame && product.Id > 0)
                .Select(product => new GogOwnedProduct(
                    product.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    string.IsNullOrWhiteSpace(product.Title) ? $"GOG product {product.Id}" : product.Title.Trim(),
                    ImageUrl(product.Image),
                    product.WorksOn?.Windows ?? false,
                    product.WorksOn?.Mac ?? false,
                    product.WorksOn?.Linux ?? false,
                    accountId)));

            if (page >= result.TotalPages || result.Products.Count == 0)
                break;
        }

        return products;
    }

    /// <summary>Reads the installers a product offers.</summary>
    public async Task<GogProductDetails> GetProductDownloadsAsync(
        Guid accountId,
        string productId,
        CancellationToken cancellationToken = default)
    {
        var token = await sessions.GetAccessTokenAsync(accountId, cancellationToken);

        return await GetAsync(
            $"{ProductUrl}{Uri.EscapeDataString(productId)}?expand=downloads",
            token,
            GogJsonContext.Default.GogProductDetails,
            cancellationToken);
    }

    /// <summary>Exchanges an installer file's downlink for the address it can be fetched from.</summary>
    public async Task<GogDownlink> ResolveDownlinkAsync(
        Guid accountId,
        string downlink,
        CancellationToken cancellationToken = default)
    {
        var token = await sessions.GetAccessTokenAsync(accountId, cancellationToken);

        var resolved = await GetAsync(downlink, token, GogJsonContext.Default.GogDownlink, cancellationToken);

        if (string.IsNullOrEmpty(resolved.Url))
            throw new GogApiException("GOG did not provide a download address for an installer file.");

        return resolved;
    }

    /// <summary>
    /// Builds a cover address from the protocol-relative image stem GOG returns.
    /// </summary>
    public static string? ImageUrl(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
            return null;

        var url = image.StartsWith("//", StringComparison.Ordinal) ? $"https:{image}" : image;

        // The stem carries no size; _196 is GOG's product tile.
        return Path.HasExtension(new Uri(url).AbsolutePath) ? url : $"{url}_196.jpg";
    }

    private async Task<T> GetAsync<T>(string url, string accessToken, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, cancellationToken);

        if (response.StatusCode is HttpStatusCode.Unauthorized)
            throw new GogAuthenticationException("GOG rejected the account's access token.");

        if (!response.IsSuccessStatusCode)
            throw new GogApiException(
                $"GOG answered {(int)response.StatusCode} {response.ReasonPhrase} for {new Uri(url).AbsolutePath}.",
                response.StatusCode);

        return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken)
               ?? throw new GogApiException($"GOG returned nothing for {new Uri(url).AbsolutePath}.");
    }
}
