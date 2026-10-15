using System.Globalization;
using System.Text.RegularExpressions;

namespace LANCommander.GOG.Inno.Versioning;

/// <summary>
/// The Inno Setup data-format version declared by the 64-byte ASCII identifier at the
/// start of a setup header block, e.g. <c>"Inno Setup Setup Data (5.5.7) (u)"</c>.
/// </summary>
/// <remarks>
/// The trailing <c>(u)</c> marks a Unicode build, which changes how every string in the
/// header is encoded. All GOG installers observed so far are Unicode builds.
/// </remarks>
public sealed partial record InnoVersion(int Major, int Minor, int Revision, bool Unicode)
    : IComparable<InnoVersion>
{
    /// <summary>Length of the identifier field that precedes every setup header block.</summary>
    public const int IdentifierLength = 64;

    [GeneratedRegex(
        @"\((?<major>\d+)\.(?<minor>\d+)\.(?<revision>\d+)(?:\.(?<build>\d+))?\)(?<unicode>\s*\(u\))?",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern { get; }

    /// <summary>Numeric form used for feature comparisons, e.g. 5.5.7 becomes 50507.</summary>
    public int Ordinal => (Major * 100 + Minor) * 100 + Revision;

    /// <summary>
    /// Parses the raw 64-byte identifier that introduces a setup header block.
    /// </summary>
    /// <exception cref="UnsupportedInstallerException">
    /// The bytes are not a recognisable Inno Setup data identifier.
    /// </exception>
    public static InnoVersion Parse(ReadOnlySpan<byte> identifier)
    {
        if (!TryParse(identifier, out var version, out var text) || version is null)
            throw new UnsupportedInstallerException(
                $"Not a recognisable Inno Setup data header. Identifier was \"{text}\".");

        return version;
    }

    /// <summary>
    /// Attempts to parse the raw 64-byte identifier that introduces a setup header block.
    /// </summary>
    /// <param name="identifier">The identifier bytes, NUL padded.</param>
    /// <param name="version">The parsed version, or <see langword="null"/> on failure.</param>
    /// <param name="text">The decoded identifier text, for diagnostics.</param>
    public static bool TryParse(ReadOnlySpan<byte> identifier, out InnoVersion? version, out string text)
    {
        version = null;

        var end = identifier.IndexOf((byte)0);
        var chars = end >= 0 ? identifier[..end] : identifier;

        // The identifier is always plain ASCII; anything else means we are not looking at a header.
        text = System.Text.Encoding.ASCII.GetString(chars).Trim();

        if (!text.Contains("Setup Data", StringComparison.Ordinal))
            return false;

        var match = IdentifierPattern.Match(text);

        if (!match.Success)
            return false;

        version = new InnoVersion(
            int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["revision"].Value, CultureInfo.InvariantCulture),
            match.Groups["unicode"].Success);

        return true;
    }

    /// <summary>True when this version is at least <paramref name="major"/>.<paramref name="minor"/>.<paramref name="revision"/>.</summary>
    public bool AtLeast(int major, int minor, int revision = 0)
        => Ordinal >= (major * 100 + minor) * 100 + revision;

    /// <summary>True when this version is below <paramref name="major"/>.<paramref name="minor"/>.<paramref name="revision"/>.</summary>
    public bool Below(int major, int minor, int revision = 0)
        => !AtLeast(major, minor, revision);

    /// <summary>Orders versions by <see cref="Ordinal"/>.</summary>
    public int CompareTo(InnoVersion? other)
        => other is null ? 1 : Ordinal.CompareTo(other.Ordinal);

    /// <summary>Renders the version and whether it is a Unicode build.</summary>
    public override string ToString()
        => $"{Major}.{Minor}.{Revision}{(Unicode ? " (Unicode)" : " (ANSI)")}";
}
