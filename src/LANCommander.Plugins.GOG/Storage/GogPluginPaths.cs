using LANCommander.SDK;

namespace LANCommander.Plugins.GOG.Storage;

/// <summary>
/// Where the plugin keeps its own state.
/// </summary>
/// <remarks>
/// The plugin's drop-in folder is replaced wholesale on every deploy, so nothing the plugin writes
/// lives there. Its state sits under the host's data directory instead, beside the server's own.
/// </remarks>
public sealed class GogPluginPaths(string root)
{
    /// <summary>Paths rooted in the host's data directory.</summary>
    public static GogPluginPaths ForHost() => new(AppPaths.GetConfigPath("PluginData", "GOG"));

    public string Root { get; } = Path.GetFullPath(root);

    public string Settings => Path.Combine(Root, "settings.json");

    public string Accounts => Path.Combine(Root, "accounts.json");

    public string Imports => Path.Combine(Root, "imports.json");

    public string Jobs => Path.Combine(Root, "jobs.json");

    /// <summary>Key ring protecting the stored GOG tokens.</summary>
    public string Keys => Path.Combine(Root, "keys");

    /// <summary>Default scratch space for downloads and extraction.</summary>
    public string DefaultWork => Path.Combine(Root, "Work");
}
