namespace LANCommander.GOG.Script.Emit;

/// <summary>
/// Maps Inno Setup's script API onto the PowerShell shim module.
/// </summary>
/// <remarks>
/// <para>
/// Names carry an <c>Inno</c> prefix in the shim because PowerShell resolves aliases before
/// functions: a shim named <c>Copy</c> or <c>Delete</c> would lose every call to the built-in
/// aliases for <c>Copy-Item</c> and <c>Remove-Item</c>.
/// </para>
/// <para>
/// Only the mechanical part of Inno's API is here — files, the registry, INI files, strings,
/// paths, and processes. The wizard, its controls, and anything that reads the running installer
/// have no meaning outside one, and a translated script gets a stub that says so rather than a
/// function that quietly returns nothing.
/// </para>
/// </remarks>
public static class InnoApi
{
    /// <summary>The module a translated script imports.</summary>
    public const string ModuleFileName = "LANCommander.Inno.psm1";

    private static readonly Dictionary<string, string> Implemented = new(StringComparer.OrdinalIgnoreCase)
    {
        // strings and paths
        ["EXPANDCONSTANT"] = "InnoExpandConstant",
        ["ADDBACKSLASH"] = "InnoAddBackslash",
        ["REMOVEBACKSLASH"] = "InnoRemoveBackslash",
        ["REMOVEBACKSLASHUNLESSROOT"] = "InnoRemoveBackslashUnlessRoot",
        ["EXTRACTFILENAME"] = "InnoExtractFileName",
        ["EXTRACTFILEEXT"] = "InnoExtractFileExt",
        ["EXTRACTFILEDIR"] = "InnoExtractFileDir",
        ["EXTRACTFILEPATH"] = "InnoExtractFilePath",
        ["EXTRACTFILEDRIVE"] = "InnoExtractFileDrive",
        ["CHANGEFILEEXT"] = "InnoChangeFileExt",
        ["REMOVEQUOTES"] = "InnoRemoveQuotes",
        ["UPPERCASE"] = "InnoUppercase",
        ["LOWERCASE"] = "InnoLowercase",
        ["ANSIUPPERCASE"] = "InnoAnsiUppercase",
        ["ANSILOWERCASE"] = "InnoAnsiLowercase",
        ["TRIM"] = "InnoTrim",
        ["LENGTH"] = "InnoLength",
        ["POS"] = "InnoPos",
        ["COPY"] = "InnoCopy",
        ["DELETE"] = "InnoDelete",
        ["INSERT"] = "InnoInsert",
        ["STRINGOFCHAR"] = "InnoStringOfChar",
        ["STRINGCHANGEEX"] = "InnoStringChangeEx",
        ["INTTOSTR"] = "InnoIntToStr",
        ["STRTOINT"] = "InnoStrToInt",
        ["STRTOINTDEF"] = "InnoStrToIntDef",
        ["ROUND"] = "InnoRound",
        ["FORMAT"] = "InnoFormat",
        ["SETLENGTH"] = "InnoSetLength",
        ["SETARRAYLENGTH"] = "InnoSetArrayLength",
        ["GETARRAYLENGTH"] = "InnoGetArrayLength",
        ["INC"] = "InnoInc",
        ["DEC"] = "InnoDec",

        // files and directories
        ["FILEEXISTS"] = "InnoFileExists",
        ["DIREXISTS"] = "InnoDirExists",
        ["FILEORDIREXISTS"] = "InnoFileOrDirExists",
        ["FILESIZE"] = "InnoFileSize",
        ["FILECOPY"] = "InnoFileCopy",
        ["DELETEFILE"] = "InnoDeleteFile",
        ["RENAMEFILE"] = "InnoRenameFile",
        ["CREATEDIR"] = "InnoCreateDir",
        ["FORCEDIRECTORIES"] = "InnoForceDirectories",
        ["REMOVEDIR"] = "InnoRemoveDir",
        ["DELTREE"] = "InnoDelTree",
        ["FINDFIRST"] = "InnoFindFirst",
        ["FINDNEXT"] = "InnoFindNext",
        ["FINDCLOSE"] = "InnoFindClose",
        ["LOADSTRINGSFROMFILE"] = "InnoLoadStringsFromFile",
        ["SAVESTRINGTOFILE"] = "InnoSaveStringToFile",
        ["GETSPACEONDISK"] = "InnoGetSpaceOnDisk",
        ["EXTRACTTEMPORARYFILE"] = "InnoExtractTemporaryFile",

        // the registry
        ["REGKEYEXISTS"] = "InnoRegKeyExists",
        ["REGVALUEEXISTS"] = "InnoRegValueExists",
        ["REGQUERYSTRINGVALUE"] = "InnoRegQueryStringValue",
        ["REGQUERYDWORDVALUE"] = "InnoRegQueryDWordValue",
        ["REGWRITESTRINGVALUE"] = "InnoRegWriteStringValue",
        ["REGWRITEDWORDVALUE"] = "InnoRegWriteDWordValue",
        ["REGDELETEKEYINCLUDINGSUBKEYS"] = "InnoRegDeleteKeyIncludingSubkeys",
        ["REGGETSUBKEYNAMES"] = "InnoRegGetSubkeyNames",
        ["REGGETVALUENAMES"] = "InnoRegGetValueNames",

        // ini files
        ["GETINISTRING"] = "InnoGetIniString",
        ["GETINIINT"] = "InnoGetIniInt",
        ["GETINIBOOL"] = "InnoGetIniBool",
        ["INIKEYEXISTS"] = "InnoIniKeyExists",
        ["SETINISTRING"] = "InnoSetIniString",
        ["SETINIBOOL"] = "InnoSetIniBool",

        // processes and the installer itself
        ["EXEC"] = "InnoExec",
        ["SHELLEXEC"] = "InnoShellExec",
        ["SLEEP"] = "InnoSleep",
        ["LOG"] = "InnoLog",
        ["MSGBOX"] = "InnoMsgBox",
        ["ABORT"] = "InnoAbort",
        ["PARAMCOUNT"] = "InnoParamCount",
        ["PARAMSTR"] = "InnoParamStr",
        ["ISWIN64"] = "InnoIsWin64",
        ["IS64BITINSTALLMODE"] = "InnoIs64BitInstallMode",
        ["ISUNINSTALLER"] = "InnoIsUninstaller",
        ["WIZARDSILENT"] = "InnoWizardSilent",
        ["GETDATETIMESTRING"] = "InnoGetDateTimeString",
        ["GETWINDOWSVERSION"] = "InnoGetWindowsVersion",
        ["GETWINDOWSVERSIONEX"] = "InnoGetWindowsVersionEx",
    };

    /// <summary>
    /// The Inno functions whose result is a Boolean.
    /// </summary>
    /// <remarks>
    /// Pascal Script stores a Boolean as a byte and does not record which a function returns, so
    /// without this list a translation cannot tell a logical operator from a bitwise one and has to
    /// defer every such decision to the shim at runtime.
    /// </remarks>
    private static readonly HashSet<string> BooleanResults = new(StringComparer.OrdinalIgnoreCase)
    {
        "FILEEXISTS", "DIREXISTS", "FILEORDIREXISTS", "FILESIZE", "FILECOPY", "DELETEFILE", "RENAMEFILE",
        "CREATEDIR", "FORCEDIRECTORIES", "REMOVEDIR", "DELTREE", "FINDFIRST", "FINDNEXT",
        "LOADSTRINGSFROMFILE", "SAVESTRINGTOFILE", "GETSPACEONDISK",
        "REGKEYEXISTS", "REGVALUEEXISTS", "REGQUERYSTRINGVALUE", "REGQUERYDWORDVALUE",
        "REGWRITESTRINGVALUE", "REGWRITEDWORDVALUE", "REGDELETEKEYINCLUDINGSUBKEYS",
        "REGGETSUBKEYNAMES", "REGGETVALUENAMES",
        "INIKEYEXISTS", "GETINIBOOL", "SETINISTRING", "SETINIBOOL",
        "EXEC", "SHELLEXEC", "ISWIN64", "IS64BITINSTALLMODE", "ISUNINSTALLER", "WIZARDSILENT",
        "CHECKFORMUTEXES", "ISADMINLOGGEDON", "ISPOWERUSERLOGGEDON", "UNLOADDLL", "VARISNULL",
    };

    /// <summary>True when an Inno function is known to return a Boolean.</summary>
    public static bool ReturnsBoolean(string innoName) => BooleanResults.Contains(innoName);

    /// <summary>The shim function standing in for an Inno function, or <c>null</c> when none does.</summary>
    public static string? Resolve(string innoName) =>
        Implemented.TryGetValue(innoName, out var shim) ? shim : null;

    /// <summary>The name a translated script calls an unimplemented Inno function by.</summary>
    /// <remarks>
    /// The script defines the stub itself, so output stays self-contained and the failure names the
    /// function that was missing rather than a resolution error.
    /// </remarks>
    public static string StubFor(string innoName) => "Inno" + Capitalise(innoName);

    private static string Capitalise(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();

    /// <summary>Reads the shim module's text out of the assembly.</summary>
    public static string ModuleText()
    {
        using var stream = typeof(InnoApi).Assembly
            .GetManifestResourceStream($"LANCommander.GOG.Script.Emit.Shim.{ModuleFileName}")
            ?? throw new InvalidOperationException($"The {ModuleFileName} resource is missing from the assembly.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
