namespace LANCommander.GOG.Manifest;

/// <summary>A directory the installer would create.</summary>
/// <param name="Name">Directory path, with Inno constants left unresolved.</param>
/// <param name="Attributes">Win32 file attributes applied to the directory.</param>
/// <param name="Conditions">Guards controlling whether the entry applies.</param>
public sealed record DirectoryEntry(string Name, uint Attributes, EntryConditions Conditions);

/// <summary>A shortcut the installer would create.</summary>
/// <param name="Name">Shortcut path, typically beneath <c>{group}</c> or <c>{commondesktop}</c>.</param>
/// <param name="Target">Command the shortcut launches.</param>
/// <param name="Parameters">Command-line arguments passed to the target.</param>
/// <param name="WorkingDirectory">Working directory the target starts in.</param>
/// <param name="IconFilename">File supplying the shortcut icon.</param>
/// <param name="IconIndex">Index of the icon within <paramref name="IconFilename"/>.</param>
/// <param name="Comment">Shortcut comment shown by the shell.</param>
/// <param name="AppUserModelId">Explicit Application User Model ID, when set.</param>
/// <param name="ShowCommand">Window show state, as a Win32 <c>SW_*</c> value.</param>
/// <param name="Conditions">Guards controlling whether the entry applies.</param>
public sealed record ShortcutEntry(
    string Name,
    string Target,
    string Parameters,
    string WorkingDirectory,
    string IconFilename,
    int IconIndex,
    string Comment,
    string AppUserModelId,
    int ShowCommand,
    EntryConditions Conditions);

/// <summary>A value the installer would write into an INI file.</summary>
/// <param name="Filename">Path of the INI file.</param>
/// <param name="Section">Section within the file.</param>
/// <param name="Key">Key written, or empty to address the section itself.</param>
/// <param name="Value">Value written.</param>
/// <param name="Conditions">Guards controlling whether the entry applies.</param>
public sealed record IniEntry(
    string Filename,
    string Section,
    string Key,
    string Value,
    EntryConditions Conditions);

/// <summary>When a <see cref="RunEntry"/> executes.</summary>
public enum RunStage
{
    /// <summary>Executed at the end of installation.</summary>
    Install,

    /// <summary>Executed at the start of uninstallation.</summary>
    Uninstall,
}

/// <summary>
/// A command the installer would execute. Captured for inspection only; this library never
/// runs it.
/// </summary>
/// <param name="Target">Program or document to execute.</param>
/// <param name="Parameters">Command-line arguments.</param>
/// <param name="WorkingDirectory">Working directory for the command.</param>
/// <param name="RunOnceId">Identifier used to de-duplicate uninstall commands.</param>
/// <param name="StatusMessage">Message shown while the command runs.</param>
/// <param name="Verb">Shell verb used to launch the target.</param>
/// <param name="Description">Description shown on the final wizard page.</param>
/// <param name="ShowCommand">Window show state, as a Win32 <c>SW_*</c> value.</param>
/// <param name="Stage">Whether the command runs on install or uninstall.</param>
/// <param name="Conditions">Guards controlling whether the entry applies.</param>
public sealed record RunEntry(
    string Target,
    string Parameters,
    string WorkingDirectory,
    string RunOnceId,
    string StatusMessage,
    string Verb,
    string Description,
    int ShowCommand,
    RunStage Stage,
    EntryConditions Conditions)
{
    /// <summary>The command rendered as it would appear on a command line.</summary>
    public string CommandLine => Parameters.Length == 0 ? Target : $"{Target} {Parameters}";
}

/// <summary>What a <see cref="DeleteEntry"/> removes.</summary>
public enum DeleteTargetType
{
    /// <summary>A single file, possibly with wildcards.</summary>
    Files,

    /// <summary>Files matching the pattern, including those in subdirectories.</summary>
    FilesAndSubdirs,

    /// <summary>An entire directory tree.</summary>
    DirAndSubdirs,
}

/// <summary>A deletion the installer would perform, before installing or when uninstalling.</summary>
/// <param name="Name">Path or wildcard pattern to remove.</param>
/// <param name="TargetType">What the pattern removes.</param>
/// <param name="Stage">Whether the deletion happens on install or uninstall.</param>
/// <param name="Conditions">Guards controlling whether the entry applies.</param>
public sealed record DeleteEntry(
    string Name,
    DeleteTargetType TargetType,
    RunStage Stage,
    EntryConditions Conditions);

/// <summary>A language the installer offers, together with its message table.</summary>
/// <param name="Name">Internal language name, e.g. <c>english</c>.</param>
/// <param name="DisplayName">Name shown in the language dialog.</param>
/// <param name="LanguageId">Windows LCID for the language.</param>
/// <param name="IsRightToLeft">True for right-to-left languages.</param>
/// <param name="Messages">
/// The language's compiled message table, captured verbatim. It is an Inno Setup messages
/// blob rather than plain text.
/// </param>
public sealed record LanguageEntry(
    string Name,
    string DisplayName,
    uint LanguageId,
    bool IsRightToLeft,
    byte[] Messages);

/// <summary>A selectable setup type, such as a full or compact installation.</summary>
/// <param name="Name">Internal name.</param>
/// <param name="Description">Description shown in the wizard.</param>
/// <param name="Size">Declared installed size, in bytes.</param>
/// <param name="IsCustom">True when this is the custom setup type.</param>
public sealed record SetupTypeEntry(string Name, string Description, long Size, bool IsCustom);

/// <summary>A selectable component of the installation.</summary>
/// <param name="Name">Internal name, used by entry conditions.</param>
/// <param name="Description">Description shown in the wizard.</param>
/// <param name="Types">Setup types this component belongs to.</param>
/// <param name="Size">Declared installed size, in bytes.</param>
public sealed record ComponentEntry(string Name, string Description, string Types, long Size);

/// <summary>An optional task the user can select, such as creating a desktop shortcut.</summary>
/// <param name="Name">Internal name, used by entry conditions.</param>
/// <param name="Description">Description shown in the wizard.</param>
/// <param name="GroupDescription">Heading the task is grouped under.</param>
/// <param name="Components">Components the task applies to.</param>
public sealed record TaskEntry(
    string Name,
    string Description,
    string GroupDescription,
    string Components);
