namespace LANCommander.Plugins.GOG.Storage;

/// <summary>Settings an administrator can change from the plugin's settings page.</summary>
public sealed record GogPluginSettings
{
    /// <summary>
    /// Scratch space for downloads, extracted trees and packages. Each import needs room for the
    /// installer, the extracted game and the package at once. Empty means the default.
    /// </summary>
    public string? WorkDirectory { get; init; }

    /// <summary>How many imports run at once. Takes effect when the server restarts.</summary>
    public int MaxConcurrentImports { get; init; } = 1;

    /// <summary>Installer language offered first when queueing an import.</summary>
    public string DefaultLanguage { get; init; } = "en";

    /// <summary>Archive storage location offered first when queueing an import.</summary>
    public Guid? DefaultStorageLocationId { get; init; }

    /// <summary>Check downloads and extracted files against the checksums GOG publishes.</summary>
    public bool VerifyChecksums { get; init; } = true;

    /// <summary>Leave each import's scratch directory in place afterwards, for troubleshooting.</summary>
    public bool KeepWorkFiles { get; init; }

    /// <summary>Ids of the server's redistributables every imported game is linked to.</summary>
    public IReadOnlyList<Guid> Redistributables { get; init; } = [];
}
