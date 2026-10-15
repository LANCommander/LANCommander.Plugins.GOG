using System.Text;
using LANCommander.GOG.Gog;
using LANCommander.GOG.Manifest;
using LANCommander.SDK.Enums;
using LANCommander.SDK.Helpers;
using Sdk = LANCommander.SDK.Models.Manifest;

namespace LANCommander.GOG.Packaging;

/// <summary>
/// Turns what an installer declares about itself into the game manifest an <c>.lcx</c> carries.
/// </summary>
/// <remarks>
/// This lives here rather than in <c>LANCommander.GOG</c> on purpose: that library promises to
/// depend on nothing, and its test suite enforces it. Anything that knows about the SDK belongs
/// on this side of the boundary.
/// </remarks>
public static class GameManifestMapper
{
    /// <summary>Provider name recorded against the GOG product id.</summary>
    public const string ExternalIdProvider = "GOG";

    /// <summary>
    /// Builds the manifest for a game read out of an installer.
    /// </summary>
    /// <param name="manifest">What the installer declares.</param>
    /// <param name="info">GOG's own metadata, when the payload carried a <c>.info</c> file.</param>
    /// <param name="installerFileName">Name of the installer, recorded as provenance.</param>
    public static Sdk.Game Map(InstallerManifest manifest, GogGameInfo? info, string installerFileName)
    {
        var title = Coalesce(info?.Name, manifest.AppName);

        var game = new Sdk.Game
        {
            Id = Guid.NewGuid(),
            ManifestVersion = VersionHelper.GetCurrentVersion().ToString(),
            Title = title,
            SortTitle = ToSortTitle(title),
            Version = Coalesce(manifest.AppVersion, manifest.Gog?.VersionName),
            // GOG builds often set the default directory to a code constant only the running
            // installer could resolve, in which case the title is the better name.
            DirectoryName = InstallerNames.PlainName(manifest.DefaultDirectoryName, title),
            // GOG installers carry no description, and guessing one would be worse than none.
            Description = string.Empty,
            Notes = $"Read from {installerFileName} (Inno Setup {manifest.InnoVersion}) by the LANCommander GOG plugin.",
            // The installer records neither a release date nor whether the game is singleplayer.
            ReleasedOn = default,
            Singleplayer = false,
            Type = GameType.MainGame,
        };

        if (!string.IsNullOrWhiteSpace(manifest.Publisher))
            game.Publishers.Add(new Sdk.Company { Name = manifest.Publisher });

        // Inno records a publisher and nothing else, so the developer list stays empty.

        // These installers only ever target Windows.
        game.Platforms.Add(new Sdk.Platform { Name = "Windows" });

        if (!string.IsNullOrWhiteSpace(manifest.ProductId))
            game.ExternalIds.Add(new Sdk.GameExternalId
            {
                Provider = ExternalIdProvider,
                ExternalId = manifest.ProductId,
            });

        foreach (var action in MapActions(manifest, info))
            game.Actions.Add(action);

        foreach (var field in MapCompatibilityFlags(info))
            game.CustomFields.Add(field);

        return game;
    }

    /// <summary>
    /// Maps GOG's launch entries onto play actions, falling back to the installer's own shortcuts
    /// when the payload carried no GOG metadata.
    /// </summary>
    public static IReadOnlyList<Sdk.Action> MapActions(InstallerManifest manifest, GogGameInfo? info)
    {
        var tasks = LaunchableTasks(info).ToList();

        return tasks.Count > 0
            ? FromPlayTasks(tasks, manifest)
            : FromShortcuts(manifest);
    }

    /// <summary>
    /// The play tasks that describe something launchable, in the order GOG declares them.
    /// </summary>
    /// <remarks>
    /// A URL task points at a store or support page rather than at the game, and a hidden task is
    /// one GOG's own client does not offer, so neither belongs in a play action list.
    /// </remarks>
    private static IEnumerable<GogPlayTask> LaunchableTasks(GogGameInfo? info)
        => (info?.PlayTasks ?? [])
            .Where(task => !task.IsHidden)
            .Where(task => !string.Equals(task.Type, "URLTask", StringComparison.OrdinalIgnoreCase))
            .Where(task => !string.IsNullOrWhiteSpace(task.Path));

    private static List<Sdk.Action> FromPlayTasks(IReadOnlyList<GogPlayTask> tasks, InstallerManifest manifest)
    {
        var actions = new List<Sdk.Action>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks)
        {
            var name = Coalesce(task.Name, manifest.AppName);

            // The importer looks an action up by name, so two of the same name are one action.
            if (!seen.Add(name))
                continue;

            actions.Add(new Sdk.Action
            {
                Name = name,
                // LaunchableTasks has already dropped anything without a path.
                Path = task.Path!,
                Arguments = task.Arguments ?? string.Empty,
                WorkingDirectory = Coalesce(task.WorkingDirectory, "{InstallDir}"),
                IsPrimaryAction = task.IsPrimary,
                SortOrder = actions.Count,
                Platforms = RuntimePlatform.Windows,
            });
        }

        return actions;
    }

    /// <summary>
    /// Reads launchers out of the installer's shortcut table, which is where a build without GOG
    /// metadata declares them.
    /// </summary>
    private static List<Sdk.Action> FromShortcuts(InstallerManifest manifest)
    {
        var actions = new List<Sdk.Action>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var shortcuts = manifest.Shortcuts
            .Where(shortcut => InnoPath.GetConstant(shortcut.Target)
                .Equals("{app}", StringComparison.OrdinalIgnoreCase));

        foreach (var shortcut in shortcuts)
        {
            var name = Coalesce(InnoPath.GetFileName(shortcut.Name), manifest.AppName);

            if (!seen.Add(name))
                continue;

            actions.Add(new Sdk.Action
            {
                Name = name,
                Path = InnoPath.ToLocalPath(shortcut.Target).Replace(Path.DirectorySeparatorChar, '\\'),
                Arguments = shortcut.Parameters,
                WorkingDirectory = string.IsNullOrWhiteSpace(shortcut.WorkingDirectory)
                    ? "{InstallDir}"
                    : shortcut.WorkingDirectory,
                IsPrimaryAction = actions.Count == 0,
                SortOrder = actions.Count,
                Platforms = RuntimePlatform.Windows,
            });
        }

        return actions;
    }

    /// <summary>
    /// Preserves the Windows compatibility flags GOG applies to a task, which an action has no
    /// field for.
    /// </summary>
    /// <remarks>
    /// Custom field names reach a launcher-side script as PowerShell variable names, so they are
    /// reduced here to something that is legal as one.
    /// </remarks>
    private static IEnumerable<Sdk.GameCustomField> MapCompatibilityFlags(GogGameInfo? info)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in LaunchableTasks(info).Where(t => !string.IsNullOrWhiteSpace(t.CompatibilityFlags)))
        {
            var name = Truncate("GogCompatibilityFlags" + Identifier(task.Name ?? string.Empty), 64);

            if (!seen.Add(name))
                continue;

            yield return new Sdk.GameCustomField(name, Truncate(task.CompatibilityFlags!, 1024));
        }
    }

    /// <summary>
    /// Moves a leading article to the end, so that titles sort where a reader expects them to.
    /// </summary>
    public static string ToSortTitle(string title)
    {
        foreach (var article in (string[])["The ", "A ", "An "])
            if (title.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                return $"{title[article.Length..]}, {title[..(article.Length - 1)]}";

        return title;
    }

    /// <summary>Keeps only the characters that are legal in a PowerShell variable name.</summary>
    private static string Identifier(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
            if (char.IsAsciiLetterOrDigit(character))
                builder.Append(character);

        return builder.ToString();
    }

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..length];

    private static string Coalesce(string? preferred, string? fallback)
        => !string.IsNullOrWhiteSpace(preferred) ? preferred.Trim()
            : !string.IsNullOrWhiteSpace(fallback) ? fallback.Trim()
            : string.Empty;
}
