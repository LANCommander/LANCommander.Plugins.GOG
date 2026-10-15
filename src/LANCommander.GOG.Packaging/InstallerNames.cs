using LANCommander.GOG.Manifest;

namespace LANCommander.GOG.Packaging;

/// <summary>
/// Reads plain names out of the paths an installer declares.
/// </summary>
/// <remarks>
/// An installer's default directory and group are not always paths. GOG builds routinely set them
/// to a code constant such as <c>{code:GetDefaultInstallPath|SOFTWARE\GOG.com}</c>, which only the
/// running installer could resolve. Taking the last segment of one of those yields a fragment of
/// the constant rather than a directory name, so anything that is not a plain name is rejected in
/// favour of a fallback.
/// </remarks>
public static class InstallerNames
{
    /// <summary>
    /// The last segment of an installer path, when it is a name a directory could have.
    /// </summary>
    /// <param name="path">The declared path, with its Inno constants unresolved.</param>
    /// <param name="fallback">What to use when the path resolves to nothing usable.</param>
    public static string PlainName(string? path, string fallback)
    {
        if (string.IsNullOrWhiteSpace(path))
            return fallback;

        // A leading constant such as {pf} is the ordinary case and says nothing about the name.
        var remainder = path[InnoPath.GetConstant(path).Length..];
        var name = InnoPath.GetFileName(remainder).Trim();

        return IsPlain(name) ? name : fallback;
    }

    private static bool IsPlain(string name)
        => name.Length > 0
           && !name.Contains('{')
           && !name.Contains('}')
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
