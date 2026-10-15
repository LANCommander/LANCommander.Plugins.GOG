using System.Text.RegularExpressions;
using LANCommander.GOG.Inno.Structs;
using LANCommander.GOG.Manifest;

namespace LANCommander.GOG.Gog;

/// <summary>
/// Recognises GOG's conventions inside an otherwise generic Inno Setup package.
/// </summary>
internal static partial class GogEnricher
{
    /// <summary>Registry path GOG records installed games under.</summary>
    private const string GamesKeyPrefix = @"SOFTWARE\GOG.com\Games\";

    [GeneratedRegex(@"/productId=""(?<value>\d+)""", RegexOptions.CultureInvariant)]
    private static partial Regex ProductIdArgument { get; }

    [GeneratedRegex(@"/buildId=""(?<value>\d+)""", RegexOptions.CultureInvariant)]
    private static partial Regex BuildIdArgument { get; }

    [GeneratedRegex(@"/versionName=""(?<value>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex VersionNameArgument { get; }

    /// <summary>
    /// Derives GOG metadata from the parsed header, or returns <see langword="null"/> when
    /// the package carries no GOG markers.
    /// </summary>
    public static GogMetadata? Enrich(ParsedSetupHeader header)
    {
        var gameKey = header.Registry
            .FirstOrDefault(entry => entry.Subkey.StartsWith(GamesKeyPrefix, StringComparison.OrdinalIgnoreCase))
            ?.Subkey;

        var productId =
            IdentifierFromKey(gameKey)
            ?? Resolved(RegistryValue(header, "gameID"))
            ?? (IsNumeric(header.AppId) ? header.AppId : null)
            ?? FindInRunArguments(header, ProductIdArgument);

        var buildId =
            Resolved(RegistryValue(header, "buildId"))
            ?? Resolved(RegistryValue(header, "BUILD"))
            ?? FindInRunArguments(header, BuildIdArgument);

        var versionName =
            Resolved(RegistryValue(header, "ver"))
            ?? FindInRunArguments(header, VersionNameArgument)
            ?? Resolved(NullIfEmpty(header.AppVersion));

        var metadata = new GogMetadata(
            ProductId: productId,
            BuildId: buildId,
            VersionName: versionName,
            GameName: Resolved(RegistryValue(header, "GAMENAME")) ?? NullIfEmpty(header.AppName),
            RegistryKey: gameKey);

        return metadata.IsRecognised ? metadata : null;
    }

    /// <summary>
    /// Extracts the game identifier from a GOG games key. Modern installers use the numeric
    /// product id directly; older ones nest a textual id under an extra path segment.
    /// </summary>
    private static string? IdentifierFromKey(string? subkey)
    {
        if (subkey is null)
            return null;

        var segments = subkey[GamesKeyPrefix.Length..]
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return null;

        // A numeric first segment is the product id; anything else is a grouping folder such
        // as "Extras", in which case the identifier is the segment beneath it.
        return IsNumeric(segments[0])
            ? segments[0]
            : segments.Length > 1 ? Resolved(segments[1]) : null;
    }

    private static string? RegistryValue(ParsedSetupHeader header, string valueName)
        => header.Registry
            .FirstOrDefault(entry =>
                entry.Subkey.StartsWith(GamesKeyPrefix, StringComparison.OrdinalIgnoreCase)
                && entry.ValueName.Equals(valueName, StringComparison.OrdinalIgnoreCase))
            ?.ValueData is { Length: > 0 } value
            ? value
            : null;

    /// <summary>
    /// GOG passes its identifiers to helper executables on the command line, which is the
    /// only place they appear in installers that write no registry entries.
    /// </summary>
    private static string? FindInRunArguments(ParsedSetupHeader header, Regex pattern)
    {
        foreach (var run in header.Runs)
        {
            var match = pattern.Match(run.Parameters);

            if (match.Success)
                return match.Groups["value"].Value;
        }

        return null;
    }

    /// <summary>
    /// Discards values that are still Inno constants or script calls, since resolving them
    /// would mean running the installer.
    /// </summary>
    private static string? Resolved(string? value)
        => value is null || value.Contains('{') || value.Contains('}') ? null : value;

    private static bool IsNumeric(string value)
        => value.Length > 0 && value.All(char.IsAsciiDigit);

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
