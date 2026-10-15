using System.IO.Compression;
using LANCommander.SDK.Helpers;
using Microsoft.Extensions.Logging;
using Sdk = LANCommander.SDK.Models.Manifest;

namespace LANCommander.GOG.Packaging;

/// <summary>
/// Writes an <c>.lcx</c> package: a ZIP holding the manifest, the game archive, and the scripts.
/// </summary>
/// <remarks>
/// <para>
/// The layout mirrors <c>LANCommander.Packaging.LCX.LCXBuilder</c>, which is the reference
/// implementation the server's importer is written against. That builder's prebuilt-archive
/// overloads refuse a manifest that carries scripts, which is why this one exists:
/// </para>
/// <code>
/// package.lcx
/// +- Manifest.yml          exact case; the lookup is case sensitive
/// +- Archives/{Archive.Id} itself a ZIP, paths relative to the install root
/// +- Scripts/{Script.Id}   raw PowerShell, no extension
/// </code>
/// </remarks>
public static class LcxWriterService
{
    /// <summary>Recorded as the author of a package when the caller names none.</summary>
    public const string DefaultAuthor = "LANCommander.GOG";

    /// <summary>
    /// Packages a game tree and its scripts.
    /// </summary>
    /// <param name="outputPath">Where to write the <c>.lcx</c>.</param>
    /// <param name="game">The manifest, which gains its archive and script records here.</param>
    /// <param name="gameDirectory">The finished game tree to archive.</param>
    /// <param name="scripts">The scripts to carry.</param>
    /// <param name="logger">Receives per-stage progress.</param>
    /// <param name="progress">Receives how much of the game tree has been archived.</param>
    /// <param name="author">Recorded as the creator of every record the package carries.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async Task WriteAsync(
        string outputPath,
        Sdk.Game game,
        string gameDirectory,
        IReadOnlyList<ComposedScript> scripts,
        ILogger logger,
        IProgress<GameTreeProgress>? progress = null,
        string author = DefaultAuthor,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var archiveId = Guid.NewGuid();

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));

        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        await using var outputStream = File.Create(outputPath);
        using var package = new ZipArchive(outputStream, ZipArchiveMode.Create);

        logger.LogInformation("Creating game files archive");

        var uncompressedSize = await WriteGameArchiveAsync(
            package, archiveId, gameDirectory, progress, cancellationToken);

        // The outer stream has everything the inner archive produced behind it by now.
        var compressedSize = outputStream.Position;

        logger.LogInformation("Writing {Count} script(s)", scripts.Count);

        foreach (var script in scripts)
        {
            var id = Guid.NewGuid();
            var entry = package.CreateEntry($"Scripts/{id}", CompressionLevel.NoCompression);

            await using (var entryStream = entry.Open())
            await using (var writer = new StreamWriter(entryStream))
                await writer.WriteAsync(script.Contents);

            game.Scripts.Add(new Sdk.Script
            {
                Id = id,
                Type = script.Type,
                Name = script.Type.ToString(),
                RequiresAdmin = script.RequiresAdmin,
                Platforms = LANCommander.SDK.Enums.RuntimePlatform.Windows,
                CreatedOn = now,
                CreatedBy = author,
                UpdatedOn = now,
                UpdatedBy = author,
            });
        }

        game.Archives.Add(new Sdk.Archive
        {
            Id = archiveId,
            // The record id, the object key, and the entry name all agree. An empty object key
            // makes the server's ingest write to the storage directory itself.
            ObjectKey = archiveId.ToString(),
            Version = string.IsNullOrWhiteSpace(game.Version) ? "1.0" : game.Version,
            // Both sizes are advisory - the server recomputes them on ingest - but the import UI
            // shows them beforehand.
            CompressedSize = compressedSize,
            UncompressedSize = uncompressedSize,
            CreatedOn = now,
            CreatedBy = author,
            UpdatedOn = now,
            UpdatedBy = author,
        });

        game.CreatedOn = now;
        game.CreatedBy = author;
        game.UpdatedOn = now;
        game.UpdatedBy = author;

        logger.LogInformation("Writing manifest");

        var manifestEntry = package.CreateEntry(ManifestHelper.ManifestFilename, CompressionLevel.NoCompression);

        await using (var entryStream = manifestEntry.Open())
        await using (var writer = new StreamWriter(entryStream))
            await writer.WriteAsync(ManifestHelper.Serialize(game));
    }

    /// <summary>
    /// Nests the game tree as a ZIP inside the package, returning its uncompressed size.
    /// </summary>
    private static async Task<long> WriteGameArchiveAsync(
        ZipArchive package,
        Guid archiveId,
        string gameDirectory,
        IProgress<GameTreeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(gameDirectory);
        var uncompressedSize = 0L;

        var files = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .ToList();

        var bytesTotal = files.Sum(file => file.Length);

        var entry = package.CreateEntry($"Archives/{archiveId}", CompressionLevel.Optimal);

        await using var entryStream = entry.Open();
        using var inner = new ZipArchive(entryStream, ZipArchiveMode.Create, leaveOpen: true);

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[index].FullName;

            // Entry names are forward slashed per the ZIP spec; GetRelativePath hands back
            // backslashes on Windows.
            var name = Path.GetRelativePath(root, file).Replace('\\', '/');

            var innerEntry = inner.CreateEntry(name, CompressionLevel.Optimal);

            await using var innerStream = innerEntry.Open();
            await using var source = File.OpenRead(file);

            uncompressedSize += source.Length;

            await source.CopyToAsync(innerStream, cancellationToken);

            progress?.Report(new GameTreeProgress(index + 1, files.Count, uncompressedSize, bytesTotal));
        }

        return uncompressedSize;
    }
}
