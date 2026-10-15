using LANCommander.GOG.Gog;
using LANCommander.GOG.Inno.Loader;
using LANCommander.GOG.Inno.Streams;
using LANCommander.GOG.Inno.Structs;
using LANCommander.GOG.Manifest;
using LANCommander.GOG.Script;

namespace LANCommander.GOG;

/// <summary>
/// Reads a GOG game installer, exposing what it would install and the files it carries.
/// </summary>
/// <remarks>
/// <para>
/// The installer is decoded as data. Nothing inside it is ever executed, so this works on
/// any platform and leaves no side effects.
/// </para>
/// <para>
/// Opening a package reads only its setup header. File contents are decompressed on demand
/// by <see cref="OpenRead"/> or <see cref="ExtractAllAsync"/>.
/// </para>
/// </remarks>
public sealed class GogInstaller : IDisposable
{
    private readonly FileStream _stream;
    private readonly PayloadReader _payload;

    private GogInstaller(
        string path,
        FileStream stream,
        InstallerManifest manifest,
        IReadOnlyList<InstallerSlice> slices)
    {
        Path = path;
        _stream = stream;
        Manifest = manifest;
        Slices = slices;
        _payload = new PayloadReader(slices, manifest.Compression);
    }

    /// <summary>Absolute path of the installer executable.</summary>
    public string Path { get; }

    /// <summary>Everything the installer declares about what it would install.</summary>
    public InstallerManifest Manifest { get; }

    /// <summary>The files the installer carries, in declaration order.</summary>
    public IReadOnlyList<InstallerFileEntry> Files => Manifest.Files;

    /// <summary>The physical files backing the compressed payload.</summary>
    public IReadOnlyList<InstallerSlice> Slices { get; }

    /// <summary>
    /// Opens an installer and decodes its setup header.
    /// </summary>
    /// <param name="path">Path to the installer executable. Any <c>-N.bin</c> slices beside it are found automatically.</param>
    /// <exception cref="UnsupportedInstallerException">The file is not an Inno Setup installer.</exception>
    /// <exception cref="UnsupportedVersionException">The format version is not supported.</exception>
    /// <exception cref="MissingSliceException">A required payload slice is absent.</exception>
    /// <exception cref="CorruptInstallerException">The installer is truncated or damaged.</exception>
    public static GogInstaller Open(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var stream = File.OpenRead(full);

        try
        {
            var offsets = SetupLoader.Locate(stream);
            var blocks = SetupHeaderBlock.Read(stream, offsets.HeaderOffset);
            var header = SetupHeaderReader.Read(blocks);
            var slices = SliceLocator.Resolve(full, offsets);

            var manifest = BuildManifest(header);

            return new GogInstaller(full, stream, manifest, slices);
        }
        catch
        {
            stream.Dispose();

            throw;
        }
    }

    /// <summary>
    /// Opens an installer, returning <see langword="false"/> rather than throwing when the
    /// file is not one this library can read.
    /// </summary>
    public static bool TryOpen(string path, out GogInstaller? installer)
    {
        try
        {
            installer = Open(path);

            return true;
        }
        catch (Exception exception) when (exception is GogInstallerException or IOException)
        {
            installer = null;

            return false;
        }
    }

    private static InstallerManifest BuildManifest(ParsedSetupHeader header) => new()
    {
        InnoVersion = header.Version,
        AppName = header.AppName,
        AppVerName = header.AppVerName,
        AppVersion = header.AppVersion,
        AppId = header.AppId,
        Publisher = header.AppPublisher,
        Copyright = header.AppCopyright,
        PublisherUrl = header.AppPublisherUrl,
        DefaultDirectoryName = header.DefaultDirName,
        DefaultGroupName = header.DefaultGroupName,
        UninstallDisplayIcon = header.UninstallDisplayIcon,
        Compression = header.Compression,
        SlicesPerDisk = header.SlicesPerDisk,
        Directories = header.Directories,
        Files = header.Files,
        Shortcuts = header.Icons,
        IniEntries = header.Ini,
        RegistryEntries = header.Registry,
        RunEntries = header.Runs,
        DeleteEntries = header.Deletes,
        Languages = header.Languages,
        SetupTypes = header.Types,
        Components = header.Components,
        Tasks = header.Tasks,
        Scripts = new InstallerScripts
        {
            CompiledCode = header.CompiledCode,
            LicenseText = header.LicenseText,
            InfoBeforeText = header.InfoBeforeText,
            InfoAfterText = header.InfoAfterText,
        },
        Gog = GogEnricher.Enrich(header),
    };

    /// <summary>
    /// Opens a file's contents for reading, decompressing it on demand.
    /// </summary>
    /// <param name="entry">A file entry from <see cref="Files"/>.</param>
    /// <returns>A forward-only stream over the file's decompressed bytes.</returns>
    /// <exception cref="InvalidOperationException">The entry carries no payload data.</exception>
    public Stream OpenRead(InstallerFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Location is null)
            throw new InvalidOperationException(
                $"\"{entry.DestinationPath}\" has no data in this installer.");

        if (entry.IsExternal)
            throw new InvalidOperationException(
                $"\"{entry.DestinationPath}\" is an external file that is not stored in the installer.");

        return _payload.OpenRead(entry.Location);
    }

    /// <summary>
    /// Extracts every file that carries data to <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">Directory to write into; created if it does not exist.</param>
    /// <param name="options">Controls filtering, layout, and verification.</param>
    /// <param name="progress">Receives one report per file extracted.</param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    /// <returns>A summary of what was written.</returns>
    public async Task<ExtractionResult> ExtractAllAsync(
        string destination,
        ExtractionOptions? options = null,
        IProgress<ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ExtractionOptions();

        var extractor = new PayloadExtractor(_payload, Files, options);

        return await extractor.ExtractAsync(destination, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the script and metadata files GOG stores in the payload, such as
    /// <c>goggame-&lt;id&gt;.info</c>, and returns the manifest's scripts with them attached.
    /// </summary>
    public async Task<InstallerScripts> LoadPayloadScriptsAsync(CancellationToken cancellationToken = default)
    {
        var scripts = new List<PayloadScript>();

        foreach (var entry in Files.Where(IsScriptLikeFile))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var source = OpenRead(entry);
            using var buffer = new MemoryStream();

            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            scripts.Add(new PayloadScript(entry.DestinationPath, buffer.ToArray()));
        }

        return Manifest.Scripts with { PayloadScripts = scripts };
    }

    /// <summary>
    /// Decodes the installer's compiled Pascal script.
    /// </summary>
    /// <remarks>
    /// Decoding reads the bytecode as data and renders it; it never runs it. Anything the script
    /// would decide at install time still depends on state this library does not have.
    /// </remarks>
    /// <exception cref="IfpsException">The installer has no script, or its script is malformed.</exception>
    public DecompiledScript DecompileScript()
    {
        if (!Manifest.Scripts.HasCompiledCode)
            throw new IfpsException($"{System.IO.Path.GetFileName(Path)} carries no compiled script.");

        return DecompiledScript.Load(Manifest.Scripts.CompiledCode);
    }

    private static bool IsScriptLikeFile(InstallerFileEntry entry)
    {
        if (!entry.HasContent)
            return false;

        var name = InnoPath.GetFileName(entry.DestinationPath);

        return name.StartsWith("goggame-", StringComparison.OrdinalIgnoreCase)
               && (name.EndsWith(".info", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".script", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".hashdb", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Releases the file handles held by this installer.</summary>
    public void Dispose()
    {
        _payload.Dispose();
        _stream.Dispose();
    }
}
