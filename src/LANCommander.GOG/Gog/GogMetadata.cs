namespace LANCommander.GOG.Gog;

/// <summary>
/// GOG-specific facts recovered from an installer, layered on top of the generic Inno
/// Setup metadata.
/// </summary>
/// <param name="ProductId">Numeric GOG product id, e.g. <c>1649233263</c>.</param>
/// <param name="BuildId">Numeric GOG build id, when the installer records one.</param>
/// <param name="VersionName">Human-readable version, e.g. <c>2.0b</c>.</param>
/// <param name="GameName">Game name as GOG records it.</param>
/// <param name="RegistryKey">The <c>SOFTWARE\GOG.com\Games\&lt;id&gt;</c> key, when present.</param>
public sealed record GogMetadata(
    string? ProductId,
    string? BuildId,
    string? VersionName,
    string? GameName,
    string? RegistryKey)
{
    /// <summary>True when enough GOG markers were found to treat this as a GOG package.</summary>
    public bool IsRecognised => ProductId is not null || RegistryKey is not null;
}
