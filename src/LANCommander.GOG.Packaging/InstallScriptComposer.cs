using System.Text;
using LANCommander.GOG.Manifest;
using LANCommander.GOG.Script;
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
/// Composes the install script: the registry and INI writes the installer declares, and the
/// registry, INI, and file changes its compiled script makes.
/// </summary>
/// <remarks>
/// <para>
/// The archive already holds the finished game tree, so nothing here copies the game's files.
/// What is left is the work Inno Setup would have done around the copy — the declarative registry
/// and INI tables, and whatever the compiled Pascal script leaves behind as installation proceeds.
/// </para>
/// <para>
/// The compiled script is not translated whole. <see cref="ScriptEffectExtractor"/> picks out the
/// calls that change the machine, and each becomes a line of plain PowerShell, so the script
/// stays short enough to read and needs nothing beside it.
/// </para>
/// </remarks>
public static class InstallScriptComposer
{
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
        => Compose(
            manifest,
            script is null ? ScriptEffects.None : ScriptEffectExtractor.Extract(script.Unit),
            installerFileName);

    /// <summary>
    /// Composes the install script from effects already picked out of the installer's script.
    /// </summary>
    public static ComposedScript? Compose(
        InstallerManifest manifest,
        ScriptEffects effects,
        string installerFileName)
    {
        var warnings = new List<string>();
        var unresolved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        var registry = manifest.RegistryEntries
            .Where(entry => !entry.IsDeletion)
            .Concat(effects.Effects.OfType<ScriptRegistryWrite>().Select(write => write.Entry))
            .Distinct()
            .ToList();

        var ini = manifest.IniEntries
            .Where(entry => entry.Key.Length > 0)
            .Concat(effects.Effects.OfType<ScriptIniWrite>().Select(write => write.Entry))
            .Distinct()
            .ToList();

        var files = effects.Effects
            .Where(effect => effect is not ScriptRegistryWrite and not ScriptIniWrite)
            .ToList();

        if (registry.Count == 0 && ini.Count == 0 && files.Count == 0)
            return null;

        var output = new StringBuilder();

        EmitHeader(output, manifest, installerFileName);

        if (registry.Count > 0)
            EmitRegistry(output, registry, unresolved);

        if (ini.Count > 0)
            EmitIni(output, ini, unresolved);

        if (files.Count > 0)
            EmitFiles(output, files, unresolved);

        if (effects.Skipped.Count > 0)
            EmitSkipped(output, effects.Skipped);

        foreach (var constant in unresolved)
            warnings.Add(
                $"{constant} is an Inno Setup constant with no stand-in outside the installer. " +
                "It is written into the script as-is.");

        if (effects.Skipped.Count > 0)
            warnings.Add(
                $"{effects.Skipped.Count} change(s) the installer's script makes were left out of the install " +
                "script; they are listed at its end.");

        var requiresAdmin =
            registry.Any(entry => entry.Root is RegistryRoot.LocalMachine or RegistryRoot.ClassesRoot)
            || ini.Any(entry => MachineWide(entry.Filename))
            || files.Any(effect => effect switch
            {
                ScriptCreateDirectory create => MachineWide(create.Path),
                ScriptWriteFile write => MachineWide(write.Path),
                ScriptCopyFile copy => MachineWide(copy.Destination),
                _ => false,
            });

        return new ComposedScript(ScriptType.Install, output.ToString(), requiresAdmin, warnings);
    }

    /// <summary>True when a path is somewhere only an administrator can write.</summary>
    private static bool MachineWide(string path) =>
        new[] { "{win}", "{sys}", "{sd}", "{commonappdata}", "{commondocs}" }
            .Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    private static void EmitHeader(StringBuilder output, InstallerManifest manifest, string installerFileName)
    {
        output.AppendLine("<#")
            .AppendLine("    Install script generated by the LANCommander GOG plugin.")
            .AppendLine()
            .AppendLine($"    Source:       {installerFileName}")
            .AppendLine($"    Inno Setup:   {manifest.InnoVersion}")
            .AppendLine()
            .AppendLine("    The archive already holds the installed game tree, so this script places no files.")
            .AppendLine("    It makes the registry and INI writes the installer declares, and the registry, INI,")
            .AppendLine("    and file changes the installer's own script makes as installation proceeds.")
            .AppendLine()
            .AppendLine("    Conditions the installer attached to these changes are not evaluated: doing so")
            .AppendLine("    would mean running the installer. Everything it declares is applied.")
            .AppendLine("#>")
            .AppendLine();
    }

    private static void EmitRegistry(
        StringBuilder output,
        IReadOnlyList<RegistryEntry> entries,
        ICollection<string> unresolved)
    {
        output.AppendLine("#region Registry")
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
        output.AppendLine("#region INI files")
            .AppendLine()
            .AppendLine("function Set-IniValue {")
            .AppendLine("    param([string] $Path, [string] $Section, [string] $Key, [string] $Value)")
            .AppendLine()
            .AppendLine("    $lines = [System.Collections.Generic.List[string]]::new()")
            .AppendLine()
            .AppendLine("    if (Test-Path -LiteralPath $Path) { $lines.AddRange([string[]]@(Get-Content -LiteralPath $Path)) }")
            .AppendLine("    else { $null = New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force }")
            .AppendLine()
            .AppendLine("    $start = -1")
            .AppendLine("    $end = $lines.Count")
            .AppendLine()
            .AppendLine("    for ($i = 0; $i -lt $lines.Count; $i++) {")
            .AppendLine("        if ($lines[$i].Trim() -match '^\\[(.*)\\]$') {")
            .AppendLine("            if ($start -ge 0) { $end = $i; break }")
            .AppendLine("            if ($Matches[1] -eq $Section) { $start = $i }")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    if ($start -lt 0) { $lines.Add(\"[$Section]\"); $lines.Add(\"$Key=$Value\") }")
            .AppendLine("    else {")
            .AppendLine("        $line = $start + 1")
            .AppendLine("        while ($line -lt $end -and $lines[$line] -notmatch \"^\\s*$([regex]::Escape($Key))\\s*=\") { $line++ }")
            .AppendLine("        if ($line -lt $end) { $lines[$line] = \"$Key=$Value\" } else { $lines.Insert($end, \"$Key=$Value\") }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    Set-Content -LiteralPath $Path -Value $lines")
            .AppendLine("}")
            .AppendLine();

        foreach (var entry in entries)
            output.AppendLine(
                "Set-IniValue " +
                $"-Path {PowerShellText.Argument(entry.Filename, unresolved)} " +
                $"-Section {PowerShellText.Argument(entry.Section, unresolved)} " +
                $"-Key {PowerShellText.Argument(entry.Key, unresolved)} " +
                $"-Value {PowerShellText.Argument(entry.Value, unresolved)}");

        output.AppendLine()
            .AppendLine("#endregion")
            .AppendLine();
    }

    private static void EmitFiles(
        StringBuilder output,
        IReadOnlyList<ScriptEffect> effects,
        ICollection<string> unresolved)
    {
        output.AppendLine("#region Files")
            .AppendLine();

        string Argument(string path) => PowerShellText.Argument(path, unresolved);
        string Expression(string path) => PowerShellText.Expression(path, unresolved);

        foreach (var effect in effects)
        {
            switch (effect)
            {
                case ScriptCreateDirectory create:
                    output.AppendLine($"$null = New-Item -ItemType Directory -Path {Argument(create.Path)} -Force");
                    break;

                case ScriptWriteFile write:
                    // Inno writes the string's bytes as they are, which Latin-1 reproduces.
                    output.AppendLine($"$null = New-Item -ItemType Directory -Path (Split-Path -Parent {Argument(write.Path)}) -Force")
                        .AppendLine(
                            $"[System.IO.File]::{(write.Append ? "AppendAllText" : "WriteAllText")}(" +
                            $"{Expression(write.Path)}, {PowerShellText.Literal(write.Contents)}, " +
                            "[System.Text.Encoding]::GetEncoding(28591))");
                    break;

                case ScriptCopyFile copy:
                {
                    var command =
                        $"Copy-Item -LiteralPath {Argument(copy.Source)} -Destination {Argument(copy.Destination)} -Force";

                    output.AppendLine(copy.FailIfExists
                        ? $"if (-not (Test-Path -LiteralPath {Argument(copy.Destination)})) {{ {command} }}"
                        : $"if (Test-Path -LiteralPath {Argument(copy.Source)}) {{ {command} }}");
                    break;
                }

                case ScriptMoveFile move:
                    output.AppendLine(
                        $"if (Test-Path -LiteralPath {Argument(move.Source)}) {{ " +
                        $"Move-Item -LiteralPath {Argument(move.Source)} -Destination {Argument(move.Destination)} -Force }}");
                    break;

                case ScriptDeleteFiles delete:
                    // A wildcard is only honoured by -Path; anything else is taken literally.
                    output.AppendLine(delete.Path.IndexOfAny(['*', '?']) >= 0
                        ? $"Remove-Item -Path {Argument(delete.Path)} -Force -ErrorAction SilentlyContinue"
                        : $"Remove-Item -LiteralPath {Argument(delete.Path)} -Force -ErrorAction SilentlyContinue");
                    break;

                case ScriptDeleteDirectory { Recursive: true } delete:
                    output.AppendLine($"Remove-Item -LiteralPath {Argument(delete.Path)} -Recurse -Force -ErrorAction SilentlyContinue");
                    break;

                case ScriptDeleteDirectory delete:
                    // Remove-Item would prompt over a directory that is not empty; this just fails.
                    output.AppendLine($"try {{ [System.IO.Directory]::Delete({Expression(delete.Path)}) }} catch {{ }}");
                    break;
            }
        }

        output.AppendLine()
            .AppendLine("#endregion")
            .AppendLine();
    }

    private static void EmitSkipped(StringBuilder output, IReadOnlyList<SkippedScriptEffect> skipped)
    {
        output.AppendLine("# Left out of the installer's script:");

        foreach (var entry in skipped)
            output.AppendLine($"#   {entry.Function} in {entry.Routine}: {entry.Reason.ReplaceLineEndings(" ")}");
    }
}
