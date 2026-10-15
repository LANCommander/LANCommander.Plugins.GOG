namespace LANCommander.Plugins.GOG.Storage;

/// <summary>Holds the plugin's settings and persists them.</summary>
public sealed class GogSettingsStore(GogPluginPaths paths)
{
    private readonly Lock _lock = new();
    private GogPluginSettings? _settings;

    public GogPluginSettings Current
    {
        get
        {
            lock (_lock)
                return _settings ??= JsonFile.Read(paths.Settings, GogJsonContext.Default.GogPluginSettings)
                                     ?? new GogPluginSettings();
        }
    }

    /// <summary>The scratch directory imports should use.</summary>
    public string WorkDirectory
        => string.IsNullOrWhiteSpace(Current.WorkDirectory) ? paths.DefaultWork : Current.WorkDirectory;

    public void Save(GogPluginSettings settings)
    {
        var normalized = settings with
        {
            MaxConcurrentImports = Math.Clamp(settings.MaxConcurrentImports, 1, 8),
            DefaultLanguage = string.IsNullOrWhiteSpace(settings.DefaultLanguage) ? "en" : settings.DefaultLanguage.Trim(),
            WorkDirectory = string.IsNullOrWhiteSpace(settings.WorkDirectory) ? null : settings.WorkDirectory.Trim(),
        };

        lock (_lock)
        {
            JsonFile.Write(paths.Settings, normalized, GogJsonContext.Default.GogPluginSettings);

            _settings = normalized;
        }
    }
}
