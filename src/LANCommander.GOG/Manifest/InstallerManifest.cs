using LANCommander.GOG.Gog;
using LANCommander.GOG.Inno.Streams;
using LANCommander.GOG.Inno.Versioning;

namespace LANCommander.GOG.Manifest;

/// <summary>
/// Everything an installer declares about what it would install, recovered by reading the
/// package rather than running it.
/// </summary>
public sealed record InstallerManifest
{
    /// <summary>The Inno Setup data-format version the package was built with.</summary>
    public required InnoVersion InnoVersion { get; init; }

    /// <summary>Application name, as shown in the wizard.</summary>
    public required string AppName { get; init; }

    /// <summary>Application name including its version.</summary>
    public required string AppVerName { get; init; }

    /// <summary>Application version string.</summary>
    public required string AppVersion { get; init; }

    /// <summary>
    /// Inno's application identifier. GOG builds set this to the numeric product id.
    /// </summary>
    public required string AppId { get; init; }

    /// <summary>Publisher name.</summary>
    public required string Publisher { get; init; }

    /// <summary>Copyright notice.</summary>
    public required string Copyright { get; init; }

    /// <summary>Publisher home page.</summary>
    public required string PublisherUrl { get; init; }

    /// <summary>Default installation directory, with Inno constants left unresolved.</summary>
    public required string DefaultDirectoryName { get; init; }

    /// <summary>Default Start menu group name.</summary>
    public required string DefaultGroupName { get; init; }

    /// <summary>Icon shown for the application in Programs and Features.</summary>
    public required string UninstallDisplayIcon { get; init; }

    /// <summary>Compression method used by the payload chunks.</summary>
    public required PayloadCompression Compression { get; init; }

    /// <summary>Number of payload slices per disk, as recorded by the installer.</summary>
    public required int SlicesPerDisk { get; init; }

    /// <summary>Directories the installer would create.</summary>
    public required IReadOnlyList<DirectoryEntry> Directories { get; init; }

    /// <summary>Files the installer would write.</summary>
    public required IReadOnlyList<InstallerFileEntry> Files { get; init; }

    /// <summary>Shortcuts the installer would create.</summary>
    public required IReadOnlyList<ShortcutEntry> Shortcuts { get; init; }

    /// <summary>INI values the installer would write.</summary>
    public required IReadOnlyList<IniEntry> IniEntries { get; init; }

    /// <summary>Registry writes the installer would perform.</summary>
    public required IReadOnlyList<RegistryEntry> RegistryEntries { get; init; }

    /// <summary>Commands the installer would execute, on install and on uninstall.</summary>
    public required IReadOnlyList<RunEntry> RunEntries { get; init; }

    /// <summary>Deletions the installer would perform, on install and on uninstall.</summary>
    public required IReadOnlyList<DeleteEntry> DeleteEntries { get; init; }

    /// <summary>Languages the installer offers.</summary>
    public required IReadOnlyList<LanguageEntry> Languages { get; init; }

    /// <summary>Selectable setup types.</summary>
    public required IReadOnlyList<SetupTypeEntry> SetupTypes { get; init; }

    /// <summary>Selectable components.</summary>
    public required IReadOnlyList<ComponentEntry> Components { get; init; }

    /// <summary>Optional tasks offered to the user.</summary>
    public required IReadOnlyList<TaskEntry> Tasks { get; init; }

    /// <summary>The installer's custom scripting, captured without being executed.</summary>
    public required InstallerScripts Scripts { get; init; }

    /// <summary>
    /// GOG-specific metadata, present when the package could be recognised as a GOG build.
    /// </summary>
    public GogMetadata? Gog { get; init; }

    /// <summary>The GOG product id, when one could be determined.</summary>
    public string? ProductId => Gog?.ProductId;

    /// <summary>
    /// True when the payload holds the game as content-addressed blobs rather than as files
    /// at their installed paths.
    /// </summary>
    /// <remarks>
    /// Some newer GOG builds stage every game file under <c>{tmp}</c> named after its hash,
    /// and the installer's compiled script assembles the real directory tree afterwards.
    /// Such a package still extracts completely, but the extracted names are hashes.
    /// <see cref="StagedFiles"/> recovers where each of them belongs.
    /// </remarks>
    public bool UsesContentAddressedPayload
    {
        get
        {
            var stored = Files.Where(file => file.HasContent).ToList();

            if (stored.Count < 8)
                return false;

            var hashNamed = stored.Count(file => LooksHashNamed(InnoPath.GetFileName(file.DestinationPath)));

            // The staging blobs dominate such a package; ordinary installers have none.
            return hashNamed * 2 > stored.Count;
        }
    }

    /// <summary>
    /// Where each content-addressed blob actually belongs, recovered from the install hooks the
    /// file entries carry. Empty for an installer that stages nothing.
    /// </summary>
    /// <remarks>
    /// GOG's script is handed the destination as a hook parameter, and Inno Setup stores those
    /// parameters as text beside the entry, so the layout is data rather than something only the
    /// script knows. See <see cref="StagedFileLayout"/>.
    /// </remarks>
    public IReadOnlyList<StagedFile> StagedFiles => _stagedFiles ??= StagedFileLayout.Resolve(Files);

    private IReadOnlyList<StagedFile>? _stagedFiles;

    /// <summary>True for a bare lowercase hex name, which is how staged blobs are named.</summary>
    private static bool LooksHashNamed(string fileName)
        => fileName.Length >= 32
           && fileName.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    /// <summary>Registry entries written beneath a given root key and path prefix.</summary>
    public IEnumerable<RegistryEntry> RegistryEntriesUnder(RegistryRoot root, string prefix)
        => RegistryEntries.Where(entry =>
            entry.Root == root && entry.Subkey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
