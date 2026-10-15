using LANCommander.GOG.Inno.Streams;
using LANCommander.GOG.Inno.Versioning;
using LANCommander.GOG.Manifest;

namespace LANCommander.GOG.Inno.Structs;

/// <summary>
/// The decoded contents of an installer's setup header, before GOG-specific enrichment.
/// </summary>
internal sealed record ParsedSetupHeader
{
    /// <summary>Positions of the setup header's string fields, in declaration order.</summary>
    private static class Field
    {
        public const int AppName = 0;
        public const int AppVerName = 1;
        public const int AppId = 2;
        public const int AppCopyright = 3;
        public const int AppPublisher = 4;
        public const int AppPublisherUrl = 5;
        public const int AppVersion = 9;
        public const int DefaultDirName = 10;
        public const int DefaultGroupName = 11;
        public const int UninstallDisplayIcon = 15;
    }

    public required InnoVersion Version { get; init; }

    /// <summary>The setup header's string fields, in declaration order.</summary>
    public required string[] Strings { get; init; }

    public required string LicenseText { get; init; }

    public required string InfoBeforeText { get; init; }

    public required string InfoAfterText { get; init; }

    /// <summary>The compiled Pascal script, captured without being interpreted.</summary>
    public required byte[] CompiledCode { get; init; }

    public required PayloadCompression Compression { get; init; }

    public required int SlicesPerDisk { get; init; }

    public required IReadOnlyList<LanguageEntry> Languages { get; init; }

    public required IReadOnlyList<SetupTypeEntry> Types { get; init; }

    public required IReadOnlyList<ComponentEntry> Components { get; init; }

    public required IReadOnlyList<TaskEntry> Tasks { get; init; }

    public required IReadOnlyList<DirectoryEntry> Directories { get; init; }

    public required IReadOnlyList<InstallerFileEntry> Files { get; init; }

    public required IReadOnlyList<ShortcutEntry> Icons { get; init; }

    public required IReadOnlyList<IniEntry> Ini { get; init; }

    public required IReadOnlyList<RegistryEntry> Registry { get; init; }

    public required IReadOnlyList<DeleteEntry> Deletes { get; init; }

    public required IReadOnlyList<RunEntry> Runs { get; init; }

    public required IReadOnlyList<FileLocation> FileLocations { get; init; }

    private string String(int index) => index < Strings.Length ? Strings[index] : string.Empty;

    public string AppName => String(Field.AppName);

    public string AppVerName => String(Field.AppVerName);

    public string AppId => String(Field.AppId);

    public string AppCopyright => String(Field.AppCopyright);

    public string AppPublisher => String(Field.AppPublisher);

    public string AppPublisherUrl => String(Field.AppPublisherUrl);

    public string AppVersion => String(Field.AppVersion);

    public string DefaultDirName => String(Field.DefaultDirName);

    public string DefaultGroupName => String(Field.DefaultGroupName);

    public string UninstallDisplayIcon => String(Field.UninstallDisplayIcon);
}
