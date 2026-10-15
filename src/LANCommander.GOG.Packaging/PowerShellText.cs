using System.Text;

namespace LANCommander.GOG.Packaging;

/// <summary>
/// Renders strings taken out of an installer as PowerShell that means the same thing.
/// </summary>
/// <remarks>
/// Installer strings are arbitrary text that may carry quotes, dollar signs, and Inno Setup
/// constants such as <c>{app}</c>. Interpolating them into a double-quoted PowerShell string
/// would let a value execute; everything here goes into single-quoted literals instead, with
/// <c>{app}</c> spliced back in as the launcher's <c>$InstallDirectory</c>.
/// </remarks>
public static class PowerShellText
{
    /// <summary>The variable the launcher hands a script the install directory in.</summary>
    public const string InstallDirectoryVariable = "$InstallDirectory";

    /// <summary>Quotes a value as a PowerShell string literal that expands nothing.</summary>
    public static string Literal(string? value)
        => "'" + (value ?? string.Empty).Replace("'", "''") + "'";

    /// <summary>
    /// Renders an installer string as a PowerShell expression, substituting <c>{app}</c> with
    /// <see cref="InstallDirectoryVariable"/>.
    /// </summary>
    /// <param name="value">The installer string, with its constants unresolved.</param>
    /// <param name="unresolved">
    /// Receives the name of every other constant found, which stays in the output verbatim.
    /// </param>
    public static string Expression(string? value, ICollection<string> unresolved)
    {
        var parts = Split(value ?? string.Empty, unresolved);

        if (parts.Count == 0)
            return "''";

        return string.Join(" + ", parts);
    }

    /// <summary>
    /// The same as <see cref="Expression"/>, parenthesised when it is compound so that it can be
    /// passed straight to a cmdlet parameter.
    /// </summary>
    public static string Argument(string? value, ICollection<string> unresolved)
    {
        var parts = Split(value ?? string.Empty, unresolved);

        return parts.Count switch
        {
            0 => "''",
            1 => parts[0],
            _ => "(" + string.Join(" + ", parts) + ")",
        };
    }

    /// <summary>
    /// Breaks an installer string into alternating literals and variable references.
    /// </summary>
    private static List<string> Split(string value, ICollection<string> unresolved)
    {
        var parts = new List<string>();
        var literal = new StringBuilder();

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '{')
            {
                literal.Append(value[index]);

                continue;
            }

            // Inno writes a literal brace as "{{".
            if (index + 1 < value.Length && value[index + 1] == '{')
            {
                literal.Append('{');
                index++;

                continue;
            }

            var end = value.IndexOf('}', index);

            if (end < 0)
            {
                literal.Append(value[index]);

                continue;
            }

            var constant = value[(index + 1)..end];

            if (constant.Equals("app", StringComparison.OrdinalIgnoreCase))
            {
                if (literal.Length > 0)
                {
                    parts.Add(Literal(literal.ToString()));
                    literal.Clear();
                }

                parts.Add(InstallDirectoryVariable);
            }
            else
            {
                // Anything else has no stand-in here. It stays as written so that a reader can
                // see what was meant, and the build reports it rather than guessing.
                unresolved.Add("{" + constant + "}");
                literal.Append(value[index..(end + 1)]);
            }

            index = end;
        }

        if (literal.Length > 0)
            parts.Add(Literal(literal.ToString()));

        return parts;
    }

    /// <summary>
    /// Converts a Win32 registry key path to the PowerShell provider path for the same key.
    /// </summary>
    /// <remarks>
    /// Copied from <c>LANCommander.Packager.Services.ScriptGeneratorService</c>, where it is
    /// private, so that this tool stays self-contained.
    /// </remarks>
    public static string RegistryPath(string keyPath)
    {
        if (keyPath.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
            return "HKLM:\\" + keyPath["HKEY_LOCAL_MACHINE\\".Length..];

        if (keyPath.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
            return "HKCU:\\" + keyPath["HKEY_CURRENT_USER\\".Length..];

        if (keyPath.StartsWith("HKEY_CLASSES_ROOT\\", StringComparison.OrdinalIgnoreCase))
            return "HKCR:\\" + keyPath["HKEY_CLASSES_ROOT\\".Length..];

        if (keyPath.StartsWith("HKEY_USERS\\", StringComparison.OrdinalIgnoreCase))
            return "HKU:\\" + keyPath["HKEY_USERS\\".Length..];

        if (keyPath.StartsWith("HKEY_CURRENT_CONFIG\\", StringComparison.OrdinalIgnoreCase))
            return "HKCC:\\" + keyPath["HKEY_CURRENT_CONFIG\\".Length..];

        if (keyPath.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
            return "HKLM:\\" + keyPath["HKLM\\".Length..];

        if (keyPath.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
            return "HKCU:\\" + keyPath["HKCU\\".Length..];

        return keyPath;
    }
}
