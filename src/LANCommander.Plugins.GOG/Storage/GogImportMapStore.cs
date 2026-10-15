namespace LANCommander.Plugins.GOG.Storage;

/// <summary>Which LANCommander game a GOG product was last imported as.</summary>
public sealed record GogImportRecord
{
    public string ProductId { get; init; } = string.Empty;
    public Guid GameId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Version { get; init; }
    public DateTimeOffset ImportedOn { get; init; }
}

/// <summary>
/// Remembers which game each GOG product was imported as, so that importing it again updates that
/// game with a new version instead of adding a second one.
/// </summary>
public sealed class GogImportMapStore(GogPluginPaths paths)
{
    private readonly Lock _lock = new();
    private Dictionary<string, GogImportRecord>? _records;

    public event Action? Changed;

    public GogImportRecord? Get(string productId)
    {
        lock (_lock)
            return Records.GetValueOrDefault(productId);
    }

    public IReadOnlyDictionary<string, GogImportRecord> GetAll()
    {
        lock (_lock)
            return new Dictionary<string, GogImportRecord>(Records);
    }

    public void Record(GogImportRecord record)
    {
        lock (_lock)
        {
            Records[record.ProductId] = record;

            JsonFile.Write(paths.Imports, Records, GogJsonContext.Default.DictionaryStringGogImportRecord);
        }

        Changed?.Invoke();
    }

    private Dictionary<string, GogImportRecord> Records
        => _records ??= JsonFile.Read(paths.Imports, GogJsonContext.Default.DictionaryStringGogImportRecord) ?? [];
}
