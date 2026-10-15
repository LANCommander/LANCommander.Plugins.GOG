namespace LANCommander.GOG.Manifest;

/// <summary>
/// Helpers for the Windows-style paths installers record.
/// </summary>
/// <remarks>
/// These are always backslash separated regardless of the machine reading them, so
/// <see cref="System.IO.Path"/> cannot be used: on Linux it treats a backslash as an
/// ordinary character and hands back the whole string as the file name.
/// </remarks>
public static class InnoPath
{
    /// <summary>Returns the last segment of an installer path.</summary>
    public static string GetFileName(string path)
    {
        var separator = path.LastIndexOfAny(['\\', '/']);

        return separator < 0 ? path : path[(separator + 1)..];
    }

    /// <summary>Returns everything before the last segment, or an empty string.</summary>
    public static string GetDirectoryName(string path)
    {
        var separator = path.LastIndexOfAny(['\\', '/']);

        return separator < 0 ? string.Empty : path[..separator];
    }

    /// <summary>
    /// Returns the leading Inno constant, such as <c>{app}</c>, or an empty string when the
    /// path does not begin with one.
    /// </summary>
    public static string GetConstant(string path)
    {
        if (!path.StartsWith('{'))
            return string.Empty;

        var end = path.IndexOf('}');

        return end < 0 ? string.Empty : path[..(end + 1)];
    }

    /// <summary>
    /// Converts an installer path to one usable on the running platform, dropping any
    /// leading constant.
    /// </summary>
    public static string ToLocalPath(string path)
    {
        var constant = GetConstant(path);
        var relative = path[constant.Length..].TrimStart('\\', '/');

        return relative.Replace('\\', System.IO.Path.DirectorySeparatorChar);
    }
}
