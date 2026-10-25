using LANCommander.GOG.Gog;
using LANCommander.GOG.Inno.Streams;
using LANCommander.GOG.Inno.Versioning;
using LANCommander.GOG.Manifest;
using LANCommander.GOG.Packaging;
using LANCommander.GOG.Script;
using LANCommander.GOG.Script.Emit;
using LANCommander.SDK.Enums;
using LANCommander.SDK.Helpers;
using Sdk = LANCommander.SDK.Models.Manifest;

namespace LANCommander.GOG.Tests;

/// <summary>
/// Covers what LANCommander.GOG.Packager makes of an installer: the script it composes, and the
/// game manifest it maps.
/// </summary>
public class PackagerTests
{
    [Fact]
    public void TheInstallScriptNeedsNothingBesideIt()
    {
        var script = Compose(EveryEffect());

        // An .lcx script is a lone blob under Scripts/, with no sibling files to import from.
        Assert.DoesNotContain("$PSScriptRoot", script.Contents);
        Assert.DoesNotContain("Import-Module", script.Contents);
    }

    [PowerShellFact]
    public void TheComposedInstallScriptIsValidPowerShell()
        => AssertParses(Compose(EveryEffect(), Registry(RegistryRoot.LocalMachine)));

    [PowerShellFact]
    public void TheComposedUninstallScriptIsValidPowerShell()
    {
        var entry = Registry(RegistryRoot.LocalMachine) with
        {
            Options = RegistryEntryOptions.UninsDeleteValue | RegistryEntryOptions.UninsDeleteEntireKeyIfEmpty,
        };

        var script = UninstallScriptComposer.Compose(Manifest(registry: [entry]), "setup.exe");

        Assert.NotNull(script);

        AssertParses(script);
    }

    [Fact]
    public void TheScriptsChangesComeOutAsPlainPowerShell()
    {
        var script = Compose(EveryEffect());

        Assert.Contains(@"New-Item -ItemType Directory -Path ($InstallDirectory + '\saves') -Force", script.Contents);
        Assert.Contains(@"Remove-Item -LiteralPath ($InstallDirectory + '\__support') -Recurse", script.Contents);
        Assert.Contains(@"Set-IniValue -Path ($InstallDirectory + '\game.ini')", script.Contents);
        Assert.Contains(@"-Name 'Language' -Type String -Value 'en'", script.Contents);
    }

    [Fact]
    public void WhatTheScriptLeftOutIsListedAndWarnedAbout()
    {
        var effects = new ScriptEffects(
            [new ScriptCreateDirectory(@"{app}\saves")],
            [new SkippedScriptEffect("DOPOSTINSTALL", "EXEC", "runs a program, which is not carried over")]);

        var script = Compose(effects);

        Assert.Contains("#   EXEC in DOPOSTINSTALL: runs a program", script.Contents);
        Assert.Contains(script.Warnings, warning => warning.Contains("left out"));
    }

    [Fact]
    public void AFolderConstantBecomesItsEnvironmentLookup()
    {
        var script = Compose(new ScriptEffects([new ScriptCreateDirectory(@"{userdocs}\My Games\Test")], []));

        Assert.Contains(@"([Environment]::GetFolderPath('MyDocuments')) + '\My Games\Test'", script.Contents);
        Assert.Empty(script.Warnings);
    }

    [Fact]
    public void AMachineWideRegistryWriteRequiresAdmin()
    {
        var script = Compose(null, Registry(RegistryRoot.LocalMachine));

        Assert.True(script.RequiresAdmin);
    }

    [Fact]
    public void APerUserRegistryWriteDoesNotRequireAdmin()
    {
        var script = Compose(null, Registry(RegistryRoot.CurrentUser));

        Assert.False(script.RequiresAdmin);
    }

    [Fact]
    public void ComposesNothingWhenThereIsNothingToDo()
    {
        Assert.Null(InstallScriptComposer.Compose(Manifest(), script: null, "setup.exe"));
    }

    [Fact]
    public void RegistryValuesGoIntoTheScriptAsLiteralsRatherThanInterpolations()
    {
        var entry = Registry(RegistryRoot.CurrentUser) with { ValueData = "$(whoami) 'quoted'" };
        var script = Compose(null, entry);

        // A double-quoted PowerShell string would let the value run.
        Assert.Contains("'$(whoami) ''quoted'''", script.Contents);
    }

    [Fact]
    public void AnInstallerConstantWithNoStandInIsReportedRatherThanGuessedAt()
    {
        var entry = Registry(RegistryRoot.CurrentUser) with { ValueData = @"{group}\Manual.lnk" };
        var script = Compose(null, entry);

        Assert.Contains(script.Warnings, warning => warning.Contains("{group}"));
    }

    [Fact]
    public void TheAppConstantBecomesTheLauncherSInstallDirectory()
    {
        var entry = Registry(RegistryRoot.CurrentUser) with { ValueData = @"{app}\game.exe" };
        var script = Compose(null, entry);

        Assert.Contains(@"($InstallDirectory + '\game.exe')", script.Contents);
    }

    [Fact]
    public void AnEntryMarkedForRemovalProducesAnUninstallScript()
    {
        var entry = Registry(RegistryRoot.LocalMachine) with { Options = RegistryEntryOptions.UninsDeleteValue };
        var script = UninstallScriptComposer.Compose(Manifest(registry: [entry]), "setup.exe");

        Assert.NotNull(script);
        Assert.True(script.RequiresAdmin);
        Assert.Contains("Remove-ItemProperty", script.Contents);
    }

    [Fact]
    public void AnInstallerThatMarksNothingForRemovalGetsNoUninstallScript()
    {
        Assert.Null(UninstallScriptComposer.Compose(
            Manifest(registry: [Registry(RegistryRoot.CurrentUser)]), "setup.exe"));
    }

    [Fact]
    public void AKeyRemovedOnlyIfEmptyIsNotRemovedOutright()
    {
        var entry = Registry(RegistryRoot.CurrentUser) with
        {
            Options = RegistryEntryOptions.UninsDeleteEntireKeyIfEmpty,
        };

        var script = UninstallScriptComposer.Compose(Manifest(registry: [entry]), "setup.exe");

        Assert.NotNull(script);
        Assert.Contains("ValueCount -eq 0", script.Contents);
    }

    [Fact]
    public void ThePrimaryPlayTaskBecomesThePrimaryAction()
    {
        var actions = GameManifestMapper.MapActions(Manifest(), Info(
            Task("Launch", "game.exe", isPrimary: true),
            Task("Config", "config.exe")));

        Assert.Equal(2, actions.Count);

        var primary = Assert.Single(actions, action => action.IsPrimaryAction);

        Assert.Equal("Launch", primary.Name);
        Assert.Equal("game.exe", primary.Path);
    }

    [Fact]
    public void HiddenAndUrlTasksAreNotPlayActions()
    {
        var actions = GameManifestMapper.MapActions(Manifest(), Info(
            Task("Launch", "game.exe", isPrimary: true),
            Task("Support", "https://gog.com", type: "URLTask"),
            Task("Cleanup", "cleanup.exe", isHidden: true)));

        Assert.Equal(["Launch"], actions.Select(action => action.Name));
    }

    [Fact]
    public void TasksOfTheSameNameCollapseIntoOneAction()
    {
        // The importer looks an action up by name, so two of the same name cannot both survive.
        var actions = GameManifestMapper.MapActions(Manifest(), Info(
            Task("Launch", "game.exe", isPrimary: true),
            Task("Launch", "game_alt.exe")));

        var action = Assert.Single(actions);

        Assert.Equal("game.exe", action.Path);
    }

    [Fact]
    public void CompatibilityFlagsSurviveAsACustomField()
    {
        var game = GameManifestMapper.Map(
            Manifest(),
            Info(Task("Launch", "game.exe", isPrimary: true) with { CompatibilityFlags = "NT4SP5 HIGHDPIAWARE" }),
            "setup.exe");

        var field = Assert.Single(game.CustomFields);

        Assert.Equal("NT4SP5 HIGHDPIAWARE", field.Value);
        // Custom field names reach a script as PowerShell variable names.
        Assert.True(field.Name.All(char.IsAsciiLetterOrDigit));
    }

    [Fact]
    public void WithoutGogMetadataTheInstallerSOwnShortcutsBecomeActions()
    {
        var shortcut = new ShortcutEntry(
            Name: @"{group}\Play Some Game",
            Target: @"{app}\game.exe",
            Parameters: "-windowed",
            WorkingDirectory: "{app}",
            IconFilename: string.Empty,
            IconIndex: 0,
            Comment: string.Empty,
            AppUserModelId: string.Empty,
            ShowCommand: 1,
            Conditions: EntryConditions.None);

        // A shortcut to the uninstaller lives outside {app} and is not a launcher.
        var uninstaller = shortcut with { Name = @"{group}\Uninstall", Target = @"{uninstallexe}" };

        var actions = GameManifestMapper.MapActions(Manifest(shortcuts: [shortcut, uninstaller]), info: null);

        var action = Assert.Single(actions);

        Assert.Equal("Play Some Game", action.Name);
        Assert.Equal(@"game.exe", action.Path);
        Assert.True(action.IsPrimaryAction);
    }

    [Fact]
    public void TheManifestVersionIsSetSoTheServerDoesNotTreatThePackageAsLegacy()
    {
        var game = GameManifestMapper.Map(Manifest(), info: null, "setup.exe");

        Assert.False(game.IsLegacyManifest());
    }

    [Fact]
    public void TheManifestSurvivesTheServerSOwnSerializerRoundTrip()
    {
        // This is the importer's entry point: it deserializes the manifest with exactly these
        // helpers, and a blank ManifestVersion sends it down the legacy path, which drops
        // platforms, action ids, and multiplayer modes.
        var game = GameManifestMapper.Map(
            Manifest(),
            Info(Task("Launch", "game.exe", isPrimary: true)),
            "setup.exe");

        var round = ManifestHelper.Deserialize<Sdk.Game>(ManifestHelper.Serialize(game));

        Assert.False(round.IsLegacyManifest());
        Assert.Equal(game.Title, round.Title);
        Assert.Equal("Windows", Assert.Single(round.Platforms).Name);
        Assert.Equal("GOG", Assert.Single(round.ExternalIds).Provider);

        var action = Assert.Single(round.Actions);

        Assert.Equal("Launch", action.Name);
        Assert.True(action.IsPrimaryAction);
        Assert.Equal(RuntimePlatform.Windows, action.Platforms);
    }

    [Fact]
    public void ACodeConstantIsNotMistakenForADirectoryName()
    {
        // GOG's builds set this to something only the running installer could resolve. Taking its
        // last segment would name the directory "GOG.com}".
        var manifest = Manifest() with
        {
            DefaultDirectoryName = @"{code:GetDefaultInstallPath|SOFTWARE\GOG.com}",
        };

        var game = GameManifestMapper.Map(manifest, info: null, "setup.exe");

        Assert.Equal("Test Game", game.DirectoryName);
    }

    [Fact]
    public void AnOrdinaryDefaultDirectoryKeepsItsLastSegment()
    {
        var manifest = Manifest() with { DefaultDirectoryName = @"{pf}\GOG Games\Close Combat 2" };

        var game = GameManifestMapper.Map(manifest, info: null, "setup.exe");

        Assert.Equal("Close Combat 2", game.DirectoryName);
    }

    [Theory]
    [InlineData("The Witcher", "Witcher, The")]
    [InlineData("A Game", "Game, A")]
    [InlineData("An Odyssey", "Odyssey, An")]
    [InlineData("Close Combat 2", "Close Combat 2")]
    public void ALeadingArticleMovesToTheEndOfTheSortTitle(string title, string expected)
        => Assert.Equal(expected, GameManifestMapper.ToSortTitle(title));

    /// <summary>Runs PowerShell's own parser over a composed script.</summary>
    private static void AssertParses(ComposedScript script)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lancommander-gog-{Guid.NewGuid()}.ps1");

        try
        {
            File.WriteAllText(path, script.Contents);

            Assert.Equal(string.Empty, PowerShell.ParseErrors(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ComposedScript Compose(ScriptEffects? effects, params RegistryEntry[] registry)
    {
        var composed = InstallScriptComposer.Compose(
            Manifest(registry: registry), effects ?? ScriptEffects.None, "setup.exe");

        Assert.NotNull(composed);

        return composed;
    }

    /// <summary>One of each change an installer's script can carry over.</summary>
    private static ScriptEffects EveryEffect() => new(
    [
        new ScriptRegistryWrite(Registry(RegistryRoot.CurrentUser) with { ValueName = "Language", ValueData = "en" }),
        new ScriptIniWrite(new IniEntry(@"{app}\game.ini", "Settings", "Language", "en", EntryConditions.None)),
        new ScriptCreateDirectory(@"{app}\saves"),
        new ScriptWriteFile(@"{app}\EULA.txt", "It's 'quoted' and $(not) run", Append: false),
        new ScriptCopyFile(@"{app}.cfg", @"{app}.cfg", FailIfExists: true),
        new ScriptMoveFile(@"{app}\old.dat", @"{app}
ew.dat"),
        new ScriptDeleteFiles(@"{app}\*.tmp"),
        new ScriptDeleteDirectory(@"{app}\__support", Recursive: true),
        new ScriptDeleteDirectory(@"{app}\empty", Recursive: false),
    ],
    [new SkippedScriptEffect("NOTIFYGALAXY", "SHELLEXEC", "runs a program, which is not carried over")]);

    private static RegistryEntry Registry(RegistryRoot root) => new(
        Root: root,
        Subkey: @"SOFTWARE\LANCommander\Test",
        ValueName: "Path",
        ValueType: RegistryValueType.String,
        ValueData: "value",
        Permissions: -1,
        Options: RegistryEntryOptions.None,
        Conditions: EntryConditions.None);

    private static GogGameInfo Info(params GogPlayTask[] tasks) => new() { Name = "Test Game", PlayTasks = tasks };

    private static GogPlayTask Task(
        string name,
        string path,
        bool isPrimary = false,
        bool isHidden = false,
        string type = "FileTask") => new()
    {
        Name = name,
        Path = path,
        Type = type,
        IsPrimary = isPrimary,
        IsHidden = isHidden,
    };

    private static InstallerManifest Manifest(
        IReadOnlyList<RegistryEntry>? registry = null,
        IReadOnlyList<ShortcutEntry>? shortcuts = null) => new()
    {
        InnoVersion = new InnoVersion(5, 5, 7, Unicode: true),
        AppName = "Test Game",
        AppVerName = "Test Game 1.0",
        AppVersion = "1.0",
        AppId = "1234567890",
        Publisher = "Test Publisher",
        Copyright = string.Empty,
        PublisherUrl = string.Empty,
        DefaultDirectoryName = @"{pf}\GOG Games\Test Game",
        DefaultGroupName = "Test Game",
        UninstallDisplayIcon = string.Empty,
        Compression = PayloadCompression.Lzma1,
        SlicesPerDisk = 1,
        Directories = [],
        Files = [],
        Shortcuts = shortcuts ?? [],
        IniEntries = [],
        RegistryEntries = registry ?? [],
        RunEntries = [],
        DeleteEntries = [],
        Languages = [],
        SetupTypes = [],
        Components = [],
        Tasks = [],
        Scripts = new InstallerScripts
        {
            CompiledCode = [],
            LicenseText = string.Empty,
            InfoBeforeText = string.Empty,
            InfoAfterText = string.Empty,
        },
        Gog = new GogMetadata(
            ProductId: "1234567890",
            BuildId: "55",
            VersionName: "1.0",
            GameName: "Test Game",
            RegistryKey: @"SOFTWARE\GOG.com\Games\1234567890"),
    };

    private static int Occurrences(string text, string value)
    {
        var count = 0;

        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;

        return count;
    }
}
