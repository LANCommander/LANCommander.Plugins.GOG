using LANCommander.Plugins.GOG.Jobs;

namespace LANCommander.Plugins.GOG.Components;

/// <summary>Formatting shared by the plugin's pages.</summary>
internal static class GogFormat
{
    /// <summary>Installer languages GOG offers, by the codes its API uses.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
    [
        ("en", "English"),
        ("de", "German"),
        ("fr", "French"),
        ("es", "Spanish"),
        ("it", "Italian"),
        ("pl", "Polish"),
        ("ru", "Russian"),
        ("pt", "Portuguese"),
        ("br", "Portuguese (Brazil)"),
        ("cz", "Czech"),
        ("hu", "Hungarian"),
        ("nl", "Dutch"),
        ("tr", "Turkish"),
        ("jp", "Japanese"),
        ("ko", "Korean"),
        ("cn", "Chinese"),
    ];

    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;

        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} {units[unit]}" : $"{bytes:N1} {units[unit]}";
    }

    public static string Duration(TimeSpan span)
        => span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s"
            : $"{Math.Max(1, span.Seconds)}s";

    public static string StageLabel(GogImportStage stage) => stage switch
    {
        GogImportStage.Resolving => "Looking up",
        _ => stage.ToString(),
    };
}
