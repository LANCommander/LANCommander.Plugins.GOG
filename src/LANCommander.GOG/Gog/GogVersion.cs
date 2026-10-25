using System.Text.RegularExpressions;

namespace LANCommander.GOG.Gog;

/// <summary>Tidies the version strings GOG attaches to its installers.</summary>
public static partial class GogVersion
{
    /// <summary>GOG's build number, e.g. the <c>(17798)</c> in <c>2.0b (17798)</c>.</summary>
    [GeneratedRegex(@"[\s_]*\(\d+\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildSuffix { get; }

    /// <summary>
    /// Strips the build number GOG appends to a version, leaving the version the game itself
    /// goes by. A version that is nothing but a build number is left alone.
    /// </summary>
    public static string? Clean(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return version;

        var cleaned = BuildSuffix.Replace(version, "").Trim();

        return cleaned.Length > 0 ? cleaned : version.Trim();
    }
}
