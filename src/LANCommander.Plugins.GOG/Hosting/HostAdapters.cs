using LANCommander.SDK.Enums;
using LANCommander.Server.ImportExport.Services;
using LANCommander.Server.Services;

namespace LANCommander.Plugins.GOG.Hosting;

/// <summary>An archive storage location a game can be imported into.</summary>
public sealed record GogStorageLocation(Guid Id, string Path, bool Default);

/// <summary>Imports a finished package into the server.</summary>
/// <remarks>
/// The seam between the plugin and the host's import pipeline, so that everything up to the
/// import itself can run without a server behind it.
/// </remarks>
public interface IGogPackageImporter
{
    /// <summary>Imports the package and returns the id of the game it created or updated.</summary>
    Task<Guid> ImportAsync(string packagePath, Guid? storageLocationId, CancellationToken cancellationToken);
}

/// <summary>Lists the storage locations games can be imported into.</summary>
public interface IGogStorageLocations
{
    Task<IReadOnlyList<GogStorageLocation>> GetArchiveLocationsAsync();
}

/// <summary>Imports through the host's <see cref="ImportRunner"/>.</summary>
internal sealed class ImportRunnerPackageImporter(ImportRunner runner) : IGogPackageImporter
{
    public async Task<Guid> ImportAsync(string packagePath, Guid? storageLocationId, CancellationToken cancellationToken)
    {
        var result = await runner.RunFileAsync(packagePath, storageLocationId, ManifestType.Game, cancellationToken);

        return result.RecordId;
    }
}

/// <summary>Reads storage locations through the host's <see cref="StorageLocationService"/>.</summary>
internal sealed class HostStorageLocations(StorageLocationService service) : IGogStorageLocations
{
    public async Task<IReadOnlyList<GogStorageLocation>> GetArchiveLocationsAsync()
    {
        var locations = await service.GetAsync(location => location.Type == StorageLocationType.Archive);

        return locations
            .Select(location => new GogStorageLocation(location.Id, location.Path, location.Default))
            .OrderByDescending(location => location.Default)
            .ThenBy(location => location.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
