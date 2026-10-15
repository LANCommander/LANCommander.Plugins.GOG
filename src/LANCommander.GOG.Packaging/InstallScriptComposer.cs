using System.Text;
using LANCommander.GOG.Manifest;
using LANCommander.GOG.Script;
using LANCommander.GOG.Script.Emit;
using LANCommander.SDK.Enums;

namespace LANCommander.GOG.Packaging;

/// <summary>A script the package will carry, together with what composing it turned up.</summary>
/// <param name="Type">Which lifecycle hook the script runs on.</param>
/// <param name="Contents">The PowerShell the package carries.</param>
/// <param name="RequiresAdmin">True when the script writes somewhere that needs elevation.</param>
/// <param name="Warnings">
/// Things the operator should see before shipping the package, such as an installer constant
/// that has no stand-in outside the installer.
/// </param>
public sealed record ComposedScript(
    ScriptType Type,
    string Contents,
    bool RequiresAdmin,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Composes the install script: the registry and INI writes the installer declares, followed by
/// the installer's own script translated into PowerShell.
/// </summary>
/// <remarks>
/// <para>
/// The archive already holds the finished game tree, so nothing here copies files. What is left is
/// the work Inno Setup would have done around the copy — the declarative registry and INI tables,
/// and whatever the compiled Pascal script does at <c>ssPostInstall</c>.
/// </para>
/// <para>
/// The translated script normally imports its shim module from beside itself, but an
/// <c>.lcx</c> script is a lone blob under <c>Scripts/</c> with no sibling files and no
/// <c>$PSScriptRoot</c>. The shim therefore travels inside the script and is written back out at
/// run time.
/// </para>
/// </remarks>
public static class InstallScriptComposer
{
    /// <summary>Inno's <c>ssPostInstall</c>, the second member of <c>TSetupStep</c>.</summary>
    private const int PostInstallStep = 1;

    /// <summary>The routine Inno calls as installation reaches each step.</summary>
    private const string StepRoutine = "CurStepChanged";

    /// <summary>
    /// Composes the install script, or returns <see langword="null"/> when there is nothing for
    /// it to do.
    /// </summary>
    /// <param name="manifest">What the installer declares.</param>
    /// <param name="script">
    /// The installer's decompiled script, or <see langword="null"/> to leave it out.
    /// </param>
    /// <param name="installerFileName">Name of the installer, recorded in the header.</param>
    public static ComposedScript? Compose(
        InstallerManifest manifest,
        DecompiledScript? script,
        string installerFileName)
    {
        var warnings = new List<string>();
        var unresolved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        var registry = manifest.RegistryEntries.Where(entry => !entry.IsDeletion).ToList();
        var ini = manifest.IniEntries.Where(entry => entry.Key.Length > 0).ToList();

        var callsStep = script is not null && HasStepRoutine(script);

        if (registry.Count == 0 && ini.Count == 0 && !callsStep)
            return null;

        var output = new StringBuilder();

        EmitHeader(output, manifest, script, installerFileName, callsStep);

        // The INI writes go through the shim too, so it is needed whenever either half does.
        if (script is not null || ini.Count > 0)
        {
            EmitShim(output);
            EmitEnvironment(output, manifest);
        }

        if (registry.Count > 0)
            EmitRegistry(output, registry, unresolved);

        if (ini.Count > 0)
            EmitIni(output, ini, unresolved);

        if (script is not null)
        {
            EmitBody(output, script);

            if (callsStep)
                EmitStepCall(output);
        }

        foreach (var constant in unresolved)
            warnings.Add(
                $"{constant} is an Inno Setup constant with no stand-in outside the installer. " +
                "It is written into the script as-is.");

        if (script is not null)
        {
            var unstructured = script.Unit.Routines.Count(routine => !routine.IsFullyStructured);

            if (callsStep && unstructured > 0)
                warnings.Add(
                    $"{unstructured} of {script.Unit.Routines.Count} translated routines kept control flow " +
                    "PowerShell cannot express and throw where the jump was. The install script calls " +
                    $"{StepRoutine}, so it may stop partway through.");
        }

        var requiresAdmin = registry.Any(entry => entry.Root == RegistryRoot.LocalMachine);

        return new ComposedScript(ScriptType.Install, output.ToString(), requiresAdmin, warnings);
    }

    /// <summary>
    /// True when the installer's script implements the routine Inno calls on each setup step.
    /// </summary>
    /// <remarks>
    /// The emitter knows nothing of Inno's lifecycle — to it <c>CurStepChanged</c> is just another
    /// routine — so recognising it is this tool's job.
    /// </remarks>
    public static bool HasStepRoutine(DecompiledScript script)
        => script.Unit.Routines.Any(routine =>
            routine.Name.Equals(StepRoutine, StringComparison.OrdinalIgnoreCase));

    private static void EmitHeader(
        StringBuilder output,
        InstallerManifest manifest,
        DecompiledScript? script,
        string installerFileName,
        bool callsStep)
    {
        output.AppendLine("<#")
            .AppendLine("    Install script generated by the LANCommander GOG plugin.")
            .AppendLine()
            .AppendLine($"    Source:       {installerFileName}")
            .AppendLine($"    Inno Setup:   {manifest.InnoVersion}")
            .AppendLine()
            .AppendLine("    The archive already holds the installed game tree, so this script places no files.")
            .AppendLine("    It performs the registry and INI writes the installer declares, and then runs the")
            .AppendLine("    installer's own script.");

        if (script is not null)
            output.AppendLine()
                .AppendLine($"    {script.Unit.StructuredRoutineCount} of {script.Unit.Routines.Count} translated " +
                            "routines came out fully structured.")
                .AppendLine(callsStep
                    ? $"    {StepRoutine} is defined, and is called below for ssPostInstall."
                    : $"    {StepRoutine} is not defined, so nothing calls into the translated script.");

        output.AppendLine()
            .AppendLine("    Entry conditions the installer attached to these writes are not evaluated: doing so")
            .AppendLine("    would mean running the installer. Everything it declares is written.")
            .AppendLine("#>")
            .AppendLine();
    }

    /// <summary>
    /// Writes the shim module out beside the running script and imports it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A single-quoted here-string expands nothing, so the module's <c>$script:</c> variables,
    /// backticks, and quotes all survive verbatim.
    /// </para>
    /// <para>
    /// It is imported as a real module rather than inlined. The shim ends in
    /// <c>Export-ModuleMember</c>, which throws outside a module, and its <c>Set-StrictMode</c>
    /// and module state are meant to be scoped to it; inlining would leak strict mode into the
    /// launcher's session.
    /// </para>
    /// </remarks>
    private static void EmitShim(StringBuilder output)
    {
        output.AppendLine("# The Inno Setup shim the translated script runs against. An .lcx script is a lone")
            .AppendLine("# blob with no sibling files, so the module travels inside it and is written back out.")
            .AppendLine($"$innoModulePath = Join-Path ([System.IO.Path]::GetTempPath()) '{InnoApi.ModuleFileName}'")
            .AppendLine("Set-Content -LiteralPath $innoModulePath -Encoding UTF8 -Value @'")
            .AppendLine(InnoApi.ModuleText().TrimEnd('\r', '\n'))
            .AppendLine("'@")
            .AppendLine("Import-Module $innoModulePath -Force")
            .AppendLine();
    }

    /// <summary>
    /// Supplies the constants the translated script expands, mapped onto what the launcher knows.
    /// </summary>
    private static void EmitEnvironment(StringBuilder output, InstallerManifest manifest)
    {
        var group = InstallerNames.PlainName(manifest.DefaultGroupName, "LANCommander");

        output.AppendLine("# The constants the installer script expands. {src} points at the install directory")
            .AppendLine("# too, so that a script looking for a file it shipped finds the one already in place.")
            .AppendLine("$innoTemp = Join-Path ([System.IO.Path]::GetTempPath()) \"lancommander-inno-$([guid]::NewGuid())\"")
            .AppendLine("$null = New-Item -ItemType Directory -Path $innoTemp -Force")
            .AppendLine()
            .AppendLine("Initialize-InnoEnvironment -ExtractedRoot $InstallDirectory -Constants @{")
            .AppendLine("    app      = $InstallDirectory")
            .AppendLine("    src      = $InstallDirectory")
            .AppendLine("    tmp      = $innoTemp")
            .AppendLine($"    group    = {PowerShellText.Literal(group)}")
            .AppendLine("    win      = $env:SystemRoot")
            .AppendLine("    sys      = (Join-Path $env:SystemRoot 'System32')")
            .AppendLine("    pf       = ${env:ProgramFiles}")
            .AppendLine("    pf32     = ${env:ProgramFiles(x86)}")
            .AppendLine("    userdocs = [System.Environment]::GetFolderPath('MyDocuments')")
            .AppendLine("    userappdata = $env:APPDATA")
            .AppendLine("    localappdata = $env:LOCALAPPDATA")
            .AppendLine("}")
            .AppendLine();
    }

    /// <summary>
    /// Appends the translated script, less the two preamble lines this script has already done.
    /// </summary>
    private static void EmitBody(StringBuilder output, DecompiledScript script)
    {
        output.AppendLine("#region Translated from the installer's compiled Pascal script")
            .AppendLine();

        foreach (var line in script.ToPowerShell().Replace("\r\n", "\n").Split('\n'))
        {
            // Strict mode and the module import are set up above, module-scoped and once.
            if (line.StartsWith("Set-StrictMode ", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("Import-Module ", StringComparison.Ordinal))
                continue;

            output.AppendLine(line);
        }

        output.AppendLine()
            .AppendLine("#endregion")
            .AppendLine();
    }

    private static void EmitStepCall(StringBuilder output)
    {
        output.AppendLine($"# Inno's TSetupStep is (ssInstall, ssPostInstall, ssDone), so ssPostInstall is {PostInstallStep}.")
            .AppendLine("# Only the post-install step is run: installation itself already happened.")
            .AppendLine($"if (Get-Command {StepRoutine} -ErrorAction SilentlyContinue) {{ {StepRoutine} {PostInstallStep} }}")
            .AppendLine();
    }

    private static void EmitRegistry(
        StringBuilder output,
        IReadOnlyList<RegistryEntry> entries,
        ICollection<string> unresolved)
    {
        output.AppendLine("#region Registry writes the installer declares")
            .AppendLine();

        foreach (var group in entries.GroupBy(entry => entry.FullKeyPath, StringComparer.OrdinalIgnoreCase))
        {
            var key = PowerShellText.Argument(PowerShellText.RegistryPath(group.Key), unresolved);

            output.AppendLine($"$null = New-Item -Path {key} -Force");

            foreach (var entry in group.Where(entry => entry.ValueType != RegistryValueType.None))
            {
                var name = PowerShellText.Argument(entry.ValueName, unresolved);
                var value = ValueExpression(entry, unresolved);

                output.AppendLine(
                    $"Set-ItemProperty -Path {key} -Name {name} -Type {PropertyType(entry.ValueType)} -Value {value}");
            }

            output.AppendLine();
        }

        output.AppendLine("#endregion").AppendLine();
    }

    /// <summary>The PowerShell registry type matching an installer value type.</summary>
    private static string PropertyType(RegistryValueType type) => type switch
    {
        RegistryValueType.ExpandString => "ExpandString",
        RegistryValueType.DWord => "DWord",
        RegistryValueType.Binary => "Binary",
        RegistryValueType.MultiString => "MultiString",
        RegistryValueType.QWord => "QWord",
        _ => "String",
    };

    /// <summary>
    /// Renders a registry value as PowerShell of the right shape for its type.
    /// </summary>
    /// <remarks>
    /// The installer stores numbers as decimal text, binary data as a hex string, and the members
    /// of a multi-string run together separated by nulls.
    /// </remarks>
    private static string ValueExpression(RegistryEntry entry, ICollection<string> unresolved) => entry.ValueType switch
    {
        RegistryValueType.DWord or RegistryValueType.QWord =>
            long.TryParse(entry.ValueData, out var number)
                ? number.ToString()
                : "0",

        RegistryValueType.Binary =>
            "([byte[]]@(" + string.Join(
                ", ",
                Convert.FromHexString(Hex(entry.ValueData)).Select(b => $"0x{b:X2}")) + "))",

        RegistryValueType.MultiString =>
            "@(" + string.Join(
                ", ",
                entry.ValueData
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => PowerShellText.Expression(part, unresolved))) + ")",

        _ => PowerShellText.Argument(entry.ValueData, unresolved),
    };

    /// <summary>Keeps only the hex digits of a value, so an odd or spaced string still decodes.</summary>
    private static string Hex(string value)
    {
        var digits = new string(value.Where(char.IsAsciiHexDigit).ToArray());

        return digits.Length % 2 == 0 ? digits : digits[..^1];
    }

    private static void EmitIni(
        StringBuilder output,
        IReadOnlyList<IniEntry> entries,
        ICollection<string> unresolved)
    {
        output.AppendLine("#region INI writes the installer declares")
            .AppendLine();

        foreach (var entry in entries)
            output.AppendLine(
                "$null = InnoSetIniString " +
                $"{PowerShellText.Argument(entry.Section, unresolved)} " +
                $"{PowerShellText.Argument(entry.Key, unresolved)} " +
                $"{PowerShellText.Argument(entry.Value, unresolved)} " +
                $"{PowerShellText.Argument(entry.Filename, unresolved)}");

        output.AppendLine()
            .AppendLine("#endregion")
            .AppendLine();
    }
}
