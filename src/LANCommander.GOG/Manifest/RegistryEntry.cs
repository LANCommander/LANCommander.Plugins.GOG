namespace LANCommander.GOG.Manifest;

/// <summary>Root key a registry entry is written under.</summary>
public enum RegistryRoot
{
    /// <summary>Unrecognised root key.</summary>
    Unknown,

    /// <summary>HKEY_CLASSES_ROOT.</summary>
    ClassesRoot,

    /// <summary>HKEY_CURRENT_USER.</summary>
    CurrentUser,

    /// <summary>HKEY_LOCAL_MACHINE.</summary>
    LocalMachine,

    /// <summary>HKEY_USERS.</summary>
    Users,

    /// <summary>HKEY_CURRENT_CONFIG.</summary>
    CurrentConfig,

    /// <summary>HKEY_PERFORMANCE_DATA.</summary>
    PerformanceData,

    /// <summary>HKEY_DYN_DATA.</summary>
    DynData,
}

/// <summary>Type of a registry value, mirroring the Win32 <c>REG_*</c> types.</summary>
public enum RegistryValueType
{
    /// <summary>The entry creates or deletes a key and writes no value.</summary>
    None,

    /// <summary>REG_SZ.</summary>
    String,

    /// <summary>REG_EXPAND_SZ.</summary>
    ExpandString,

    /// <summary>REG_DWORD.</summary>
    DWord,

    /// <summary>REG_BINARY.</summary>
    Binary,

    /// <summary>REG_MULTI_SZ.</summary>
    MultiString,

    /// <summary>REG_QWORD.</summary>
    QWord,
}

/// <summary>Behavioural flags Inno Setup attaches to a registry entry.</summary>
[Flags]
public enum RegistryEntryOptions
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Write the value only when it does not already exist.</summary>
    CreateValueIfDoesntExist = 1 << 0,

    /// <summary>Replace an existing value of a different type.</summary>
    UninsDeleteValue = 1 << 1,

    /// <summary>Remove the whole key when the application is uninstalled.</summary>
    UninsDeleteEntireKey = 1 << 2,

    /// <summary>Remove the key when uninstalling, but only if it is then empty.</summary>
    UninsDeleteEntireKeyIfEmpty = 1 << 3,

    /// <summary>Remove the value when the application is uninstalled.</summary>
    UninsClearValue = 1 << 4,

    /// <summary>Delete the key before writing.</summary>
    DeleteKey = 1 << 5,

    /// <summary>Delete the value before writing.</summary>
    DeleteValue = 1 << 6,

    /// <summary>Do not fail the installation if the write is rejected.</summary>
    NoError = 1 << 7,

    /// <summary>Preserve an existing value rather than overwriting it.</summary>
    PreserveStringType = 1 << 8,

    /// <summary>Entry applies to the 32-bit view of the registry.</summary>
    Bit32 = 1 << 9,

    /// <summary>Entry applies to the 64-bit view of the registry.</summary>
    Bit64 = 1 << 10,
}

/// <summary>
/// A single registry write the installer would perform.
/// </summary>
/// <param name="Root">Root key the entry is written beneath.</param>
/// <param name="Subkey">Key path below the root, with Inno constants left unresolved.</param>
/// <param name="ValueName">Value name, or empty for the key's default value.</param>
/// <param name="ValueType">Type of the value written.</param>
/// <param name="ValueData">
/// Value data as written in the installer. Numeric types are decimal text and binary data
/// is a hex string; use the typed accessors to interpret it.
/// </param>
/// <param name="Permissions">Raw permission-entry index, or -1 when none applies.</param>
/// <param name="Options">Behavioural flags for the write.</param>
/// <param name="Conditions">Guards controlling whether the entry applies.</param>
public sealed record RegistryEntry(
    RegistryRoot Root,
    string Subkey,
    string ValueName,
    RegistryValueType ValueType,
    string ValueData,
    short Permissions,
    RegistryEntryOptions Options,
    EntryConditions Conditions)
{
    /// <summary>The full key path, e.g. <c>HKEY_LOCAL_MACHINE\SOFTWARE\GOG.com</c>.</summary>
    public string FullKeyPath => $"{RootKeyName}\\{Subkey}";

    /// <summary>Conventional Win32 name of <see cref="Root"/>.</summary>
    public string RootKeyName => Root switch
    {
        RegistryRoot.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegistryRoot.CurrentUser => "HKEY_CURRENT_USER",
        RegistryRoot.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryRoot.Users => "HKEY_USERS",
        RegistryRoot.CurrentConfig => "HKEY_CURRENT_CONFIG",
        RegistryRoot.PerformanceData => "HKEY_PERFORMANCE_DATA",
        RegistryRoot.DynData => "HKEY_DYN_DATA",
        _ => "HKEY_UNKNOWN",
    };

    /// <summary>True when the entry only deletes, writing no value.</summary>
    public bool IsDeletion =>
        ValueType is RegistryValueType.None
        && Options.HasFlag(RegistryEntryOptions.DeleteKey);
}
