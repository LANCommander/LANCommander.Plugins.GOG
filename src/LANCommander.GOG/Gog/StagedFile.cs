using System.Text.RegularExpressions;
using LANCommander.GOG.Manifest;

namespace LANCommander.GOG.Gog;

/// <summary>
/// A game file that the installer stages under <c>{tmp}</c> as one or more content-addressed
/// blobs, together with where it really belongs.
/// </summary>
/// <param name="DestinationPath">Where the file is installed, relative to <c>{app}</c>.</param>
/// <param name="Id">The identifier GOG's script uses for the file.</param>
/// <param name="FileIndices">
/// The payload entries whose contents make up the file, in the order they concatenate.
/// </param>
/// <param name="IsDependency">
/// True for a redistributable the installer stages for its own use rather than a game file.
/// </param>
public sealed record StagedFile(
    string DestinationPath,
    string Id,
    IReadOnlyList<int> FileIndices,
    bool IsDependency)
{
    /// <summary>True when the file arrives split across several blobs.</summary>
    public bool IsChunked => FileIndices.Count > 1;
}

/// <summary>
/// Recovers where a content-addressed payload's blobs actually belong.
/// </summary>
/// <remarks>
/// <para>
/// Some GOG builds stage every game file under <c>{tmp}</c> named after a hash, and let the
/// installer's script assemble the real directory tree afterwards. The mapping back looks like it
/// lives only inside that script — but it does not. GOG's script takes the destination as a
/// parameter, and Inno Setup stores the parameters of a <c>BeforeInstall</c> hook as text on the
/// file entry itself. Reading them recovers the layout without interpreting anything.
/// </para>
/// <para>
/// The script's own <c>before_install</c> sets the chunk count and its <c>after_install</c> merges
/// once that many blobs have arrived, so a file split across several blobs claims that many
/// consecutive entries. That is the only ordering assumption here, and it is the one the script
/// itself relies on.
/// </para>
/// </remarks>
public static partial class StagedFileLayout
{
    /// <summary>
    /// Matches GOG's staging hook: an identifier, the destination, and how many blobs it takes.
    /// </summary>
    [GeneratedRegex(
        @"^\s*before_install(?<dependency>_dependency)?\s*\(\s*'(?<id>[^']*)'\s*,\s*'(?<path>[^']*)'\s*,\s*(?<chunks>\d+)\s*\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HookPattern { get; }

    /// <summary>
    /// Reads the staging layout out of the file entries' install hooks.
    /// </summary>
    /// <param name="files">The manifest's file entries, in declaration order.</param>
    /// <returns>One entry per staged file, or an empty list when the installer stages nothing.</returns>
    public static IReadOnlyList<StagedFile> Resolve(IReadOnlyList<InstallerFileEntry> files)
    {
        var staged = new List<StagedFile>();

        for (var index = 0; index < files.Count; index++)
        {
            var match = HookPattern.Match(files[index].Conditions.BeforeInstall);

            if (!match.Success)
                continue;

            if (!int.TryParse(match.Groups["chunks"].Value, out var chunks) || chunks < 1)
                continue;

            var indices = new List<int>(chunks);

            // A chunked file claims this entry and the ones straight after it, which is the order
            // the script merges them in.
            for (var chunk = 0; chunk < chunks && index + chunk < files.Count; chunk++)
                indices.Add(files[index + chunk].Index);

            staged.Add(new StagedFile(
                DestinationPath: match.Groups["path"].Value,
                Id: match.Groups["id"].Value,
                FileIndices: indices,
                IsDependency: match.Groups["dependency"].Success));

            index += indices.Count - 1;
        }

        return staged;
    }
}
