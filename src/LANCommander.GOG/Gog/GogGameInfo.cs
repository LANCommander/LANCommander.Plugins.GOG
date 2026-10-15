using System.Text.Json;
using System.Text.Json.Serialization;

namespace LANCommander.GOG.Gog;

/// <summary>
/// The contents of a <c>goggame-&lt;id&gt;.info</c> file, which GOG ships inside the payload
/// to describe the installed game and how to launch it.
/// </summary>
public sealed record GogGameInfo
{
    /// <summary>GOG product id.</summary>
    [JsonPropertyName("gameId")]
    public string? GameId { get; init; }

    /// <summary>Identifier of the base game, which differs for DLC.</summary>
    [JsonPropertyName("rootGameId")]
    public string? RootGameId { get; init; }

    /// <summary>GOG build id.</summary>
    [JsonPropertyName("buildId")]
    public string? BuildId { get; init; }

    /// <summary>Display name of the game.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Identifier of the GOG Galaxy client build that produced the file.</summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    /// <summary>
    /// Version of GOG's metadata schema, not of the game. Every file seen so far declares 1.
    /// </summary>
    [JsonPropertyName("version")]
    public int? SchemaVersion { get; init; }

    /// <summary>The language this installer was built for.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    /// <summary>Language codes the installation supports.</summary>
    [JsonPropertyName("languages")]
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>Launch entries describing what can be run after installation.</summary>
    [JsonPropertyName("playTasks")]
    public IReadOnlyList<GogPlayTask> PlayTasks { get; init; } = [];

    /// <summary>The task marked as the game's primary launch target, if any.</summary>
    public GogPlayTask? PrimaryPlayTask
        => PlayTasks.FirstOrDefault(task => task.IsPrimary)
           ?? PlayTasks.FirstOrDefault(task => task.Category == "game");

    /// <summary>
    /// Parses a <c>goggame-&lt;id&gt;.info</c> document.
    /// </summary>
    /// <returns>The parsed metadata, or <see langword="null"/> if it is not valid JSON.</returns>
    public static GogGameInfo? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            // The files are UTF-8 and occasionally carry a byte-order mark.
            ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];

            var reader = new Utf8JsonReader(
                json.StartsWith(bom) ? json[3..] : json,
                new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            return JsonSerializer.Deserialize(ref reader, GogJsonContext.Default.GogGameInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// One launchable entry from a <c>goggame-&lt;id&gt;.info</c> file.
/// </summary>
public sealed record GogPlayTask
{
    /// <summary>Display name of the task.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Kind of task, typically <c>game</c>, <c>tool</c>, or <c>document</c>.</summary>
    [JsonPropertyName("category")]
    public string? Category { get; init; }

    /// <summary>Task type, such as <c>FileTask</c> or <c>URLTask</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Path of the executable or document, relative to the install directory.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Arguments passed when launching.</summary>
    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }

    /// <summary>Working directory for the launch, relative to the install directory.</summary>
    [JsonPropertyName("workingDir")]
    public string? WorkingDirectory { get; init; }

    /// <summary>Windows compatibility flags GOG applies, such as <c>HIGHDPIAWARE</c>.</summary>
    [JsonPropertyName("compatibilityFlags")]
    public string? CompatibilityFlags { get; init; }

    /// <summary>Target of a <c>URLTask</c>.</summary>
    [JsonPropertyName("link")]
    public string? Link { get; init; }

    /// <summary>True when this is the game's main launch target.</summary>
    [JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; init; }

    /// <summary>True when the task must be elevated.</summary>
    [JsonPropertyName("isHidden")]
    public bool IsHidden { get; init; }

    /// <summary>Languages this task applies to.</summary>
    [JsonPropertyName("languages")]
    public IReadOnlyList<string> Languages { get; init; } = [];
}

/// <summary>
/// Source-generated serialisation for GOG's metadata documents, so the library stays
/// trimming and AOT friendly.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GogGameInfo))]
[JsonSerializable(typeof(GogPlayTask))]
internal partial class GogJsonContext : JsonSerializerContext;
