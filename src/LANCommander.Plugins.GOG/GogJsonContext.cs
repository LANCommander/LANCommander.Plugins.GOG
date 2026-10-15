using System.Text.Json.Serialization;
using LANCommander.Plugins.GOG.Api;
using LANCommander.Plugins.GOG.Jobs;
using LANCommander.Plugins.GOG.Storage;

namespace LANCommander.Plugins.GOG;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GogPluginSettings))]
[JsonSerializable(typeof(List<StoredGogAccount>))]
[JsonSerializable(typeof(Dictionary<string, GogImportRecord>))]
[JsonSerializable(typeof(List<GogImportJobSnapshot>))]
[JsonSerializable(typeof(GogTokenResponse))]
[JsonSerializable(typeof(GogUserData))]
[JsonSerializable(typeof(GogFilteredProductsPage))]
[JsonSerializable(typeof(GogProductDetails))]
[JsonSerializable(typeof(GogDownlink))]
internal sealed partial class GogJsonContext : JsonSerializerContext;
