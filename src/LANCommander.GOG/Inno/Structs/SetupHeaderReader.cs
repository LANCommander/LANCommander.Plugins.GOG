using LANCommander.GOG.Inno.Streams;
using LANCommander.GOG.Inno.Versioning;
using LANCommander.GOG.Manifest;

namespace LANCommander.GOG.Inno.Structs;

/// <summary>
/// The number of records of each kind an installer declares, read from the fixed block of
/// counts that follows the setup header's string fields.
/// </summary>
internal sealed record EntryCounts(
    int Languages,
    int CustomMessages,
    int Permissions,
    int Types,
    int Components,
    int Tasks,
    int Directories,
    int Files,
    int FileLocations,
    int Icons,
    int Ini,
    int Registry,
    int InstallDelete,
    int UninstallDelete,
    int Run,
    int UninstallRun);

/// <summary>
/// Decodes the record stream inside a decompressed setup header.
/// </summary>
/// <remarks>
/// Records are stored back to back with no framing, so a single mis-sized field silently
/// corrupts everything that follows. The reader guards against that by requiring the parse
/// to finish exactly at the end of each block.
/// </remarks>
internal static class SetupHeaderReader
{
    /// <summary>Fixed size of one file location record.</summary>
    private const int FileLocationRecordLength = 74;

    /// <summary>
    /// Flags on a file location record. Inno stores them as a bit set, so each value is the
    /// bit for one option in declaration order.
    /// </summary>
    /// <remarks>
    /// Confirmed against real installers: the filter bit is set on exactly the PE binaries
    /// (<c>.exe</c>, <c>.dll</c>, <c>.cpl</c>) and on nothing else, and the compressed bit is
    /// set on every record of an LZMA2 payload.
    /// </remarks>
    private const byte CallInstructionOptimized = 0x10;

    private const byte ChunkEncrypted = 0x40;

    private const byte ChunkCompressed = 0x80;

    /// <summary>
    /// Distance from the end of the setup header's trailing fixed block back to the
    /// compression method, which is stable across every format version observed.
    /// </summary>
    private const int CompressMethodOffsetFromEnd = 19;

    /// <summary>Distance from the end of that block back to the slices-per-disk field.</summary>
    private const int SlicesPerDiskOffsetFromEnd = 28;

    /// <summary>
    /// Size of the trailing fixed block for versions that declare a setup mutex, and for
    /// the older layout that does not.
    /// </summary>
    private const int TrailingBlockLength = 93;

    private const int LegacyTrailingBlockLength = 96;

    /// <summary>
    /// Reads both header blocks into a manifest.
    /// </summary>
    public static ParsedSetupHeader Read(SetupHeaderBlocks blocks)
    {
        var version = blocks.Version;

        if (!version.Unicode)
            throw new UnsupportedVersionException(
                $"Inno Setup {version} is an ANSI build. Only Unicode builds are supported, " +
                "which covers every GOG installer seen so far.")
            {
                DetectedVersion = version,
            };

        var reader = new InnoStructReader(blocks.Header, version);

        var strings = ReadHeaderStrings(reader, version);
        var texts = new[] { reader.ReadAnsiString(), reader.ReadAnsiString(), reader.ReadAnsiString() };
        var compiledCode = reader.ReadAnsiBytes();
        var counts = ReadCounts(reader);

        var trailing = ReadTrailingBlock(reader, version, counts);

        var languages = ReadLanguages(reader, counts.Languages);

        SkipCustomMessages(reader, counts.CustomMessages);
        SkipPermissions(reader, counts.Permissions);

        var types = ReadTypes(reader, counts.Types);
        var components = ReadComponents(reader, counts.Components);
        var tasks = ReadTasks(reader, counts.Tasks);
        var directories = ReadDirectories(reader, counts.Directories);
        var files = ReadFiles(reader, counts.Files);
        var icons = ReadIcons(reader, counts.Icons);
        var ini = ReadIni(reader, counts.Ini);
        var registry = ReadRegistry(reader, counts.Registry);

        var deletes = new List<DeleteEntry>(counts.InstallDelete + counts.UninstallDelete);

        deletes.AddRange(ReadDeletes(reader, counts.InstallDelete, RunStage.Install));
        deletes.AddRange(ReadDeletes(reader, counts.UninstallDelete, RunStage.Uninstall));

        var runs = new List<RunEntry>(counts.Run + counts.UninstallRun);

        runs.AddRange(ReadRuns(reader, counts.Run, RunStage.Install));
        runs.AddRange(ReadRuns(reader, counts.UninstallRun, RunStage.Uninstall));

        // Anything left in the first block is the wizard imagery, which we do not model.
        var locations = ReadFileLocations(blocks.FileLocations, counts.FileLocations, version);

        return new ParsedSetupHeader
        {
            Version = version,
            Strings = strings,
            LicenseText = texts[0],
            InfoBeforeText = texts[1],
            InfoAfterText = texts[2],
            CompiledCode = compiledCode,
            Compression = trailing.Compression,
            SlicesPerDisk = trailing.SlicesPerDisk,
            Languages = languages,
            Types = types,
            Components = components,
            Tasks = tasks,
            Directories = directories,
            Files = AttachLocations(files, locations),
            Icons = icons,
            Ini = ini,
            Registry = registry,
            Deletes = deletes,
            Runs = runs,
            FileLocations = locations,
        };
    }

    /// <summary>
    /// Reads the setup header's string fields. Later format versions append fields, so the
    /// count is version dependent.
    /// </summary>
    private static string[] ReadHeaderStrings(InnoStructReader reader, InnoVersion version)
    {
        // Through CloseApplicationsFilter, present in every supported version.
        var count = 27;

        // SetupMutex.
        if (version.AtLeast(5, 5, 6))
            count++;

        // ChangesEnvironment and ChangesAssociations became expressions rather than flags.
        if (version.AtLeast(5, 6))
            count += 2;

        var strings = new string[count];

        for (var i = 0; i < count; i++)
            strings[i] = reader.ReadString();

        return strings;
    }

    private static EntryCounts ReadCounts(InnoStructReader reader) => new(
        Languages: reader.ReadInt32(),
        CustomMessages: reader.ReadInt32(),
        Permissions: reader.ReadInt32(),
        Types: reader.ReadInt32(),
        Components: reader.ReadInt32(),
        Tasks: reader.ReadInt32(),
        Directories: reader.ReadInt32(),
        Files: reader.ReadInt32(),
        FileLocations: reader.ReadInt32(),
        Icons: reader.ReadInt32(),
        Ini: reader.ReadInt32(),
        Registry: reader.ReadInt32(),
        InstallDelete: reader.ReadInt32(),
        UninstallDelete: reader.ReadInt32(),
        Run: reader.ReadInt32(),
        UninstallRun: reader.ReadInt32());

    /// <summary>
    /// Consumes the fixed block of scalar settings that closes the setup header, returning
    /// the two fields that affect how the payload is read.
    /// </summary>
    /// <remarks>
    /// The block's size varies by format version and most of its contents describe wizard
    /// appearance, which this library does not model. Its length is therefore determined by
    /// checking which candidate leaves the reader positioned on a plausible first language
    /// entry, so an unseen version degrades to a clear error rather than silent corruption.
    /// </remarks>
    private static (PayloadCompression Compression, int SlicesPerDisk) ReadTrailingBlock(
        InnoStructReader reader,
        InnoVersion version,
        EntryCounts counts)
    {
        var expected = version.AtLeast(5, 5, 6) ? TrailingBlockLength : LegacyTrailingBlockLength;
        var length = Calibrate(reader, counts, expected);

        var block = reader.ReadBytes(length);

        var compression = block[^CompressMethodOffsetFromEnd] switch
        {
            0 => PayloadCompression.Stored,
            1 => PayloadCompression.ZLib,
            2 => PayloadCompression.BZip2,
            3 => PayloadCompression.Lzma1,
            4 => PayloadCompression.Lzma2,
            _ => PayloadCompression.Unknown,
        };

        var slices = BitConverter.ToInt32(block, block.Length - SlicesPerDiskOffsetFromEnd);

        return (compression, slices < 1 ? 1 : slices);
    }

    /// <summary>
    /// Finds the trailing block's length by testing which candidate leaves the reader on a
    /// well-formed language entry.
    /// </summary>
    private static int Calibrate(InnoStructReader reader, EntryCounts counts, int expected)
    {
        // Without a language entry to land on there is nothing to check against.
        if (counts.Languages <= 0)
            return expected;

        if (LooksLikeLanguageEntry(reader, expected))
            return expected;

        // Search outwards so the closest plausible layout wins.
        for (var delta = 1; delta <= 24; delta++)
        {
            if (LooksLikeLanguageEntry(reader, expected + delta))
                return expected + delta;

            if (expected - delta > 0 && LooksLikeLanguageEntry(reader, expected - delta))
                return expected - delta;
        }

        throw new UnsupportedVersionException(
            $"Could not locate the first language entry in an Inno Setup {reader.Version} header. " +
            "The setup header layout for this version is not recognised.")
        {
            DetectedVersion = reader.Version,
        };
    }

    /// <summary>
    /// Tests whether a language entry begins <paramref name="skip"/> bytes ahead, without
    /// disturbing the reader's position.
    /// </summary>
    private static bool LooksLikeLanguageEntry(InnoStructReader reader, int skip)
    {
        if (skip < 0 || skip > reader.Remaining)
            return false;

        var probe = reader.Fork(skip);

        try
        {
            // A language's internal name is a short lowercase identifier such as "english".
            var name = probe.ReadString();

            if (name.Length is 0 or > 32 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                return false;

            // Its display name follows, then four font names, all short and printable.
            for (var i = 0; i < 5; i++)
            {
                var value = probe.ReadString();

                if (value.Length > 64 || value.Any(char.IsControl))
                    return false;
            }

            return true;
        }
        catch (GogInstallerException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the guards attached to most installable entries.
    /// </summary>
    /// <remarks>
    /// The two script hooks are stored after-then-before, which is the reverse of how they read in
    /// a script. Both innoextract and GOG's own entries agree: every GOG file entry names
    /// <c>before_install</c> in the second of the two.
    /// </remarks>
    private static EntryConditions ReadConditions(InnoStructReader reader)
    {
        var components = reader.ReadString();
        var tasks = reader.ReadString();
        var languages = reader.ReadString();
        var check = reader.ReadString();
        var afterInstall = reader.ReadString();
        var beforeInstall = reader.ReadString();

        return new EntryConditions(
            Components: components,
            Tasks: tasks,
            Languages: languages,
            Check: check,
            BeforeInstall: beforeInstall,
            AfterInstall: afterInstall,
            MinVersion: reader.ReadWindowsVersion(),
            OnlyBelowVersion: reader.ReadWindowsVersion());
    }

    private static List<LanguageEntry> ReadLanguages(InnoStructReader reader, int count)
    {
        var entries = new List<LanguageEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            var displayName = reader.ReadString();

            // Dialog, title, welcome, and copyright font names.
            for (var font = 0; font < 4; font++)
                reader.ReadString();

            var messages = reader.ReadAnsiBytes();

            // Per-language licence and info texts.
            for (var text = 0; text < 3; text++)
                reader.ReadAnsiBytes();

            var languageId = reader.ReadUInt32();

            // Dialog, title, welcome, and copyright font sizes.
            for (var size = 0; size < 4; size++)
                reader.ReadInt32();

            var rightToLeft = reader.ReadBoolean();

            entries.Add(new LanguageEntry(name, displayName, languageId, rightToLeft, messages));
        }

        return entries;
    }

    private static void SkipCustomMessages(InnoStructReader reader, int count)
    {
        for (var i = 0; i < count; i++)
        {
            reader.ReadString();
            reader.ReadString();
            reader.ReadInt32();
        }
    }

    private static void SkipPermissions(InnoStructReader reader, int count)
    {
        for (var i = 0; i < count; i++)
            reader.ReadAnsiBytes();
    }

    private static List<SetupTypeEntry> ReadTypes(InnoStructReader reader, int count)
    {
        var entries = new List<SetupTypeEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            var description = reader.ReadString();

            reader.ReadString();  // Languages
            reader.ReadString();  // Check

            reader.ReadWindowsVersion();
            reader.ReadWindowsVersion();

            var options = reader.ReadByte();
            var kind = reader.ReadByte();
            var size = reader.ReadInt64();

            // The custom setup type is the one that lets the user pick components.
            entries.Add(new SetupTypeEntry(name, description, size, IsCustom: kind == 1 || (options & 1) != 0));
        }

        return entries;
    }

    private static List<ComponentEntry> ReadComponents(InnoStructReader reader, int count)
    {
        var entries = new List<ComponentEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            var description = reader.ReadString();
            var types = reader.ReadString();

            reader.ReadString();  // Languages
            reader.ReadString();  // Check

            reader.ReadWindowsVersion();
            reader.ReadWindowsVersion();

            reader.ReadInt64();   // ExtraDiskSpaceRequired
            reader.ReadInt32();   // Level
            reader.ReadByte();    // Used
            reader.ReadByte();    // Options

            var size = reader.ReadInt64();

            entries.Add(new ComponentEntry(name, description, types, size));
        }

        return entries;
    }

    private static List<TaskEntry> ReadTasks(InnoStructReader reader, int count)
    {
        var entries = new List<TaskEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            var description = reader.ReadString();
            var groupDescription = reader.ReadString();
            var components = reader.ReadString();

            reader.ReadString();  // Languages
            reader.ReadString();  // Check

            reader.ReadWindowsVersion();
            reader.ReadWindowsVersion();

            reader.ReadInt32();   // Level
            reader.ReadByte();    // Used
            reader.ReadByte();    // Options

            entries.Add(new TaskEntry(name, description, groupDescription, components));
        }

        return entries;
    }

    private static List<DirectoryEntry> ReadDirectories(InnoStructReader reader, int count)
    {
        var entries = new List<DirectoryEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            var conditions = ReadConditions(reader);
            var attributes = reader.ReadUInt32();

            reader.ReadUInt16();  // Permission index
            reader.ReadByte();    // Options

            entries.Add(new DirectoryEntry(name, attributes, conditions));
        }

        return entries;
    }

    private static List<(InstallerFileEntry Entry, uint LocationIndex)> ReadFiles(InnoStructReader reader, int count)
    {
        var entries = new List<(InstallerFileEntry, uint)>(count);

        for (var i = 0; i < count; i++)
        {
            var sourcePath = reader.ReadString();
            var destinationPath = reader.ReadString();

            reader.ReadString();  // InstallFontName
            reader.ReadString();  // StrongAssemblyName

            var conditions = ReadConditions(reader);

            var locationIndex = reader.ReadUInt32();
            var attributes = reader.ReadUInt32();

            reader.ReadInt64();   // ExternalSize
            reader.ReadUInt16();  // Permission index

            var options = reader.ReadUInt32();
            var kind = reader.ReadByte();

            entries.Add((new InstallerFileEntry(
                Index: i,
                DestinationPath: destinationPath,
                SourcePath: sourcePath,
                Location: null,
                Attributes: attributes,
                Kind: kind switch
                {
                    1 => InstallerFileKind.UninstallerExe,
                    2 => InstallerFileKind.RegSvrExe,
                    _ => InstallerFileKind.File,
                },

                // An entry with no location entry has no bytes in the installer.
                IsExternal: locationIndex == uint.MaxValue,
                RawOptions: options,
                Conditions: conditions),
                locationIndex));
        }

        return entries;
    }

    /// <summary>
    /// Resolves each file entry's location index against the table from the second block.
    /// </summary>
    private static List<InstallerFileEntry> AttachLocations(
        List<(InstallerFileEntry Entry, uint LocationIndex)> files,
        IReadOnlyList<FileLocation> locations)
    {
        var result = new List<InstallerFileEntry>(files.Count);

        foreach (var (entry, locationIndex) in files)
            result.Add(entry with
            {
                Location = locationIndex < locations.Count ? locations[(int)locationIndex] : null,
            });

        return result;
    }

    private static List<ShortcutEntry> ReadIcons(InnoStructReader reader, int count)
    {
        var entries = new List<ShortcutEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            var target = reader.ReadString();
            var parameters = reader.ReadString();
            var workingDirectory = reader.ReadString();
            var iconFilename = reader.ReadString();
            var comment = reader.ReadString();
            var appUserModelId = reader.ReadString();

            var conditions = ReadConditions(reader);

            var iconIndex = reader.ReadInt32();
            var showCommand = reader.ReadInt32();

            reader.ReadByte();    // CloseOnExit
            reader.ReadUInt16();  // HotKey
            reader.ReadByte();    // Options

            entries.Add(new ShortcutEntry(
                name, target, parameters, workingDirectory, iconFilename,
                iconIndex, comment, appUserModelId, showCommand, conditions));
        }

        return entries;
    }

    private static List<IniEntry> ReadIni(InnoStructReader reader, int count)
    {
        var entries = new List<IniEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var filename = reader.ReadString();
            var section = reader.ReadString();
            var key = reader.ReadString();
            var value = reader.ReadString();

            var conditions = ReadConditions(reader);

            reader.ReadByte();  // Options

            entries.Add(new IniEntry(filename, section, key, value, conditions));
        }

        return entries;
    }

    private static List<RegistryEntry> ReadRegistry(InnoStructReader reader, int count)
    {
        var entries = new List<RegistryEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var subkey = reader.ReadString();
            var valueName = reader.ReadString();
            var valueData = reader.ReadString();

            var conditions = ReadConditions(reader);

            var rootKey = reader.ReadUInt32();
            var permissions = (short)reader.ReadUInt16();
            var valueType = reader.ReadByte();
            var flags = reader.ReadBytes(2);

            entries.Add(new RegistryEntry(
                Root: MapRootKey(rootKey),
                Subkey: subkey,
                ValueName: valueName,
                ValueType: valueType switch
                {
                    1 => RegistryValueType.String,
                    2 => RegistryValueType.ExpandString,
                    3 => RegistryValueType.DWord,
                    4 => RegistryValueType.Binary,
                    5 => RegistryValueType.MultiString,
                    6 => RegistryValueType.QWord,
                    _ => RegistryValueType.None,
                },
                ValueData: valueData,
                Permissions: permissions,
                Options: MapRegistryOptions(flags),
                Conditions: conditions));
        }

        return entries;
    }

    private static RegistryRoot MapRootKey(uint rootKey) => (rootKey & 0x7FFFFFFF) switch
    {
        0 => RegistryRoot.ClassesRoot,
        1 => RegistryRoot.CurrentUser,
        2 => RegistryRoot.LocalMachine,
        3 => RegistryRoot.Users,
        4 => RegistryRoot.PerformanceData,
        5 => RegistryRoot.CurrentConfig,
        6 => RegistryRoot.DynData,
        _ => RegistryRoot.Unknown,
    };

    private static RegistryEntryOptions MapRegistryOptions(byte[] flags)
    {
        var options = RegistryEntryOptions.None;

        if ((flags[0] & 0x01) != 0)
            options |= RegistryEntryOptions.CreateValueIfDoesntExist;

        if ((flags[0] & 0x02) != 0)
            options |= RegistryEntryOptions.UninsDeleteValue;

        if ((flags[0] & 0x04) != 0)
            options |= RegistryEntryOptions.UninsClearValue;

        if ((flags[0] & 0x08) != 0)
            options |= RegistryEntryOptions.UninsDeleteEntireKey;

        if ((flags[0] & 0x10) != 0)
            options |= RegistryEntryOptions.UninsDeleteEntireKeyIfEmpty;

        if ((flags[0] & 0x20) != 0)
            options |= RegistryEntryOptions.PreserveStringType;

        if ((flags[0] & 0x40) != 0)
            options |= RegistryEntryOptions.DeleteKey;

        if ((flags[0] & 0x80) != 0)
            options |= RegistryEntryOptions.DeleteValue;

        if ((flags[1] & 0x01) != 0)
            options |= RegistryEntryOptions.NoError;

        return options;
    }

    private static List<DeleteEntry> ReadDeletes(InnoStructReader reader, int count, RunStage stage)
    {
        var entries = new List<DeleteEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            var conditions = ReadConditions(reader);
            var kind = reader.ReadByte();

            entries.Add(new DeleteEntry(
                name,
                kind switch
                {
                    1 => DeleteTargetType.FilesAndSubdirs,
                    2 => DeleteTargetType.DirAndSubdirs,
                    _ => DeleteTargetType.Files,
                },
                stage,
                conditions));
        }

        return entries;
    }

    private static List<RunEntry> ReadRuns(InnoStructReader reader, int count, RunStage stage)
    {
        var entries = new List<RunEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var target = reader.ReadString();
            var parameters = reader.ReadString();
            var workingDirectory = reader.ReadString();
            var runOnceId = reader.ReadString();
            var statusMessage = reader.ReadString();
            var verb = reader.ReadString();
            var description = reader.ReadString();

            var conditions = ReadConditions(reader);

            var showCommand = reader.ReadInt32();

            reader.ReadByte();    // Wait
            reader.ReadUInt16();  // Options

            entries.Add(new RunEntry(
                target, parameters, workingDirectory, runOnceId, statusMessage, verb,
                description, showCommand, stage, conditions));
        }

        return entries;
    }

    /// <summary>
    /// Reads the fixed-size file location records that make up the second header block.
    /// </summary>
    private static List<FileLocation> ReadFileLocations(byte[] block, int count, InnoVersion version)
    {
        var expected = (long)count * FileLocationRecordLength;

        if (block.Length < expected)
            throw new CorruptInstallerException(
                $"File location table holds {block.Length} bytes but {count} records need {expected}.");

        var reader = new InnoStructReader(block, version);
        var entries = new List<FileLocation>(count);

        for (var i = 0; i < count; i++)
        {
            var firstSlice = reader.ReadUInt32();
            var lastSlice = reader.ReadUInt32();
            var chunkOffset = reader.ReadUInt32();
            var offsetInChunk = reader.ReadInt64();
            var originalSize = reader.ReadInt64();
            var chunkCompressedSize = reader.ReadInt64();
            var sha1 = reader.ReadBytes(20);
            var timestamp = reader.ReadInt64();

            reader.ReadUInt32();  // FileVersionMS
            reader.ReadUInt32();  // FileVersionLS

            var flags = reader.ReadBytes(2);

            entries.Add(new FileLocation(
                Index: i,
                FirstSlice: firstSlice,
                LastSlice: lastSlice,
                ChunkOffset: chunkOffset,
                OffsetInChunk: offsetInChunk,
                OriginalSize: originalSize,
                ChunkCompressedSize: chunkCompressedSize,
                Sha1: sha1,
                Timestamp: ToDateTime(timestamp),
                IsCompressed: (flags[0] & ChunkCompressed) != 0,
                IsFiltered: (flags[0] & CallInstructionOptimized) != 0,
                IsChunkEncrypted: (flags[0] & ChunkEncrypted) != 0));
        }

        return entries;
    }

    private static DateTime ToDateTime(long fileTime)
    {
        try
        {
            return fileTime <= 0 ? default : DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return default;
        }
    }
}
