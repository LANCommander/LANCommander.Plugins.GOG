using LANCommander.GOG.Gog;
using LANCommander.GOG.Script;
using Microsoft.Extensions.Logging;
using Sdk = LANCommander.SDK.Models.Manifest;

namespace LANCommander.GOG.Packaging;

/// <summary>The part of building a package that is under way.</summary>
public enum GogPackageStage
{
    /// <summary>Producing the finished game tree from the installer.</summary>
    Extracting,

    /// <summary>Writing the game tree, scripts and manifest into the package.</summary>
    Packaging,
}

/// <summary>How far building a package has got.</summary>
public readonly record struct GogPackageProgress(GogPackageStage Stage, GameTreeProgress Progress);

/// <summary>What to build, and how.</summary>
public sealed record GogPackageOptions
{
    /// <summary>
    /// Check every extracted file against the checksum the installer records for it. On by default.
    /// </summary>
    public bool VerifyChecksums { get; init; } = true;

    /// <summary>
    /// Carry the registry, INI, and file changes the installer's compiled script makes into the
    /// install script. On by default.
    /// </summary>
    public bool IncludeScript { get; init; } = true;

    /// <summary>
    /// The id to give the game record, so that importing the package updates a game imported
    /// before rather than adding another.
    /// </summary>
    public Guid? GameId { get; init; }

    /// <summary>Title to fall back on when the installer names none.</summary>
    public string? FallbackTitle { get; init; }

    /// <summary>GOG product id to record when the installer does not carry one.</summary>
    public string? FallbackProductId { get; init; }

    /// <summary>Recorded as the creator of every record in the package.</summary>
    public string Author { get; init; } = LcxWriterService.DefaultAuthor;
}

/// <summary>What building a package produced.</summary>
/// <param name="OutputPath">Where the package was written.</param>
/// <param name="Game">The manifest the package carries.</param>
/// <param name="Tree">What producing the game tree wrote.</param>
/// <param name="Warnings">Things worth a look before the package is relied on.</param>
public sealed record GogPackageResult(
    string OutputPath,
    Sdk.Game Game,
    GameTreeResult Tree,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Turns a GOG installer into an <c>.lcx</c> package: extracts the finished game tree, maps the
/// installer's metadata onto a game manifest, composes the install and uninstall scripts, and
/// writes the lot out.
/// </summary>
public static class GogPackageBuilder
{
    /// <summary>
    /// Builds a package from the installer at <paramref name="installerPath"/>.
    /// </summary>
    /// <param name="installerPath">The installer executable. Its <c>-N.bin</c> slices must sit beside it.</param>
    /// <param name="treeDirectory">Scratch directory the game tree is extracted into.</param>
    /// <param name="outputPath">Where to write the package.</param>
    /// <param name="options">What to build, and how.</param>
    /// <param name="logger">Receives what the build finds along the way.</param>
    /// <param name="progress">Receives how far the build has got.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <exception cref="GogInstallerException">The installer could not be read.</exception>
    public static async Task<GogPackageResult> BuildAsync(
        string installerPath,
        string treeDirectory,
        string outputPath,
        GogPackageOptions options,
        ILogger logger,
        IProgress<GogPackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var installer = GogInstaller.Open(installerPath);

        var manifest = installer.Manifest;
        var installerFileName = Path.GetFileName(installer.Path);

        logger.LogInformation(
            "Opened {Installer}: {AppName} {Version} (Inno Setup {InnoVersion})",
            installerFileName, manifest.AppName, manifest.AppVersion, manifest.InnoVersion);

        var tree = await GameTreeExtractor.ExtractAsync(
            installer,
            treeDirectory,
            options.VerifyChecksums,
            logger,
            Relay(progress, GogPackageStage.Extracting),
            cancellationToken);

        var info = await LoadGameInfoAsync(installer, treeDirectory, logger, cancellationToken);
        var script = options.IncludeScript ? LoadScript(installer, logger) : null;

        var game = GameManifestMapper.Map(manifest, info, installerFileName);

        ApplyOptions(game, options);

        var scripts = new List<ComposedScript>();

        if (InstallScriptComposer.Compose(manifest, script, installerFileName) is { } install)
            scripts.Add(install);

        if (UninstallScriptComposer.Compose(manifest, installerFileName) is { } uninstall)
            scripts.Add(uninstall);

        await LcxWriterService.WriteAsync(
            outputPath,
            game,
            treeDirectory,
            scripts,
            logger,
            Relay(progress, GogPackageStage.Packaging),
            options.Author,
            cancellationToken);

        var warnings = scripts.SelectMany(composed => composed.Warnings).Distinct().ToList();

        logger.LogInformation(
            "Wrote {Output}: {Files} files, {Megabytes:N0} MB, {Actions} action(s), {Scripts} script(s)",
            outputPath,
            tree.FilesWritten,
            tree.BytesWritten / 1_000_000d,
            game.Actions.Count,
            scripts.Count);

        return new GogPackageResult(outputPath, game, tree, warnings);
    }

    private static void ApplyOptions(Sdk.Game game, GogPackageOptions options)
    {
        if (options.GameId is { } id && id != Guid.Empty)
            game.Id = id;

        if (string.IsNullOrWhiteSpace(game.Title) && !string.IsNullOrWhiteSpace(options.FallbackTitle))
        {
            game.Title = options.FallbackTitle.Trim();
            game.SortTitle = GameManifestMapper.ToSortTitle(game.Title);

            if (string.IsNullOrWhiteSpace(game.DirectoryName))
                game.DirectoryName = InstallerNames.PlainName(null, game.Title);
        }

        var hasProductId = game.ExternalIds.Any(external =>
            string.Equals(external.Provider, GameManifestMapper.ExternalIdProvider, StringComparison.OrdinalIgnoreCase));

        if (!hasProductId && !string.IsNullOrWhiteSpace(options.FallbackProductId))
            game.ExternalIds.Add(new Sdk.GameExternalId
            {
                Provider = GameManifestMapper.ExternalIdProvider,
                ExternalId = options.FallbackProductId,
            });
    }

    /// <summary>
    /// Reads GOG's own metadata, which describes the game and how to launch it.
    /// </summary>
    /// <remarks>
    /// It is usually a named file in the payload. A content-addressed installer stores it as a
    /// hash-named blob like everything else, so there it is only recognisable once the tree has
    /// been assembled - which is why the extracted tree is the second place to look.
    /// </remarks>
    private static async Task<GogGameInfo?> LoadGameInfoAsync(
        GogInstaller installer,
        string treeDirectory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var scripts = await installer.LoadPayloadScriptsAsync(cancellationToken);

        var entry = scripts.PayloadScripts
            .FirstOrDefault(script => script.FileName.EndsWith(".info", StringComparison.OrdinalIgnoreCase));

        if (entry is not null)
            return Parse(entry.Content, entry.FileName, logger);

        var assembled = Directory
            .EnumerateFiles(treeDirectory, "goggame-*.info", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (assembled is null)
        {
            logger.LogInformation(
                "Found no GOG metadata; play actions will come from the installer's shortcuts.");

            return null;
        }

        return Parse(await File.ReadAllBytesAsync(assembled, cancellationToken), Path.GetFileName(assembled), logger);
    }

    private static GogGameInfo? Parse(ReadOnlySpan<byte> content, string fileName, ILogger logger)
    {
        var info = GogGameInfo.Parse(content);

        if (info is null)
            logger.LogWarning("{FileName} is not valid JSON and was ignored.", fileName);

        return info;
    }

    /// <summary>
    /// Decodes the installer's compiled script, unless it has none or it is in a form the
    /// decompiler does not recognise.
    /// </summary>
    private static DecompiledScript? LoadScript(GogInstaller installer, ILogger logger)
    {
        if (!installer.Manifest.Scripts.HasCompiledCode)
        {
            logger.LogInformation("The installer carries no compiled script.");

            return null;
        }

        if (!installer.Manifest.Scripts.CompiledCodeIsRecognised)
        {
            logger.LogWarning(
                "The installer's script is in an unfamiliar form and was left out of the package.");

            return null;
        }

        return installer.DecompileScript();
    }

    private static IProgress<GameTreeProgress>? Relay(IProgress<GogPackageProgress>? progress, GogPackageStage stage)
        => progress is null
            ? null
            : new SynchronousProgress<GameTreeProgress>(update => progress.Report(new GogPackageProgress(stage, update)));
}
