using LANCommander.SDK.Plugins;

namespace LANCommander.Plugins.GOG.Storage;

/// <summary>
/// Where the plugin keeps its own state.
/// </summary>
/// <remarks>
/// Everything lives in the <c>Data</c> folder inside the plugin's own folder, which the server keeps
/// when it installs an update over the plugin and deletes when the plugin is uninstalled.
/// </remarks>
public sealed class GogPluginPaths(string root)
{
    /// <summary>Paths in the plugin's <c>Data</c> folder.</summary>
    public static GogPluginPaths ForHost() => new(PluginPaths.GetDataDirectory<GogPluginPaths>());

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
