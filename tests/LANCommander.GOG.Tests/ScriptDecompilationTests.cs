using System.Diagnostics;
using LANCommander.GOG.Script.Ast;
using LANCommander.GOG.Script.Emit;

namespace LANCommander.GOG.Tests;

/// <summary>
/// Decompiles the scripts real GOG installers carry.
/// </summary>
/// <remarks>
/// There is no source to compare against, so these tests hold the decompiler to what can be
/// checked: every routine comes out, nearly all of them come out structured, the output names the
/// entry points Inno Setup actually calls, and the PowerShell it produces is valid PowerShell.
/// </remarks>
public class ScriptDecompilationTests
{
    /// <summary>
    /// The share of routines expected to reduce to loops and conditionals.
    /// </summary>
    /// <remarks>
    /// Every sample sits near 90%. The floor is set below that so ordinary variation does not fail
    /// the build, but close enough that losing a control-flow shape does.
    /// </remarks>
    private const double StructuredFloor = 0.85;

    [InstallerTheory]
    [MemberData(nameof(ScriptDecodingTests.Samples), MemberType = typeof(ScriptDecodingTests))]
    public void EveryRoutineIsRecovered(string sample)
    {
        if (SampleInstallers.Find(sample) is not { } path)
            return;

        using var package = GogInstaller.Open(path);

        var unit = package.DecompileScript().Unit;

        Assert.NotEmpty(unit.Routines);
        Assert.All(unit.Routines, routine => Assert.Null(routine.Error));
        Assert.All(unit.Routines, routine => Assert.NotEmpty(routine.Name));

        Assert.True(
            unit.StructuredFraction >= StructuredFloor,
            $"Only {unit.StructuredFraction:P1} of routines reduced to loops and conditionals.");
    }

    [InstallerFact(SampleInstallers.CloseCombat2)]
    public void FindsTheEntryPointsInnoSetupCalls()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.CloseCombat2)!);

        var unit = package.DecompileScript().Unit;
        var names = unit.Routines.Select(routine => routine.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("InitializeSetup", names);
        Assert.Contains("CurStepChanged", names);
        Assert.Contains("NextButtonClick", names);

        var initialize = unit.Routines.First(routine => routine.Name.Equals("InitializeSetup", StringComparison.OrdinalIgnoreCase));

        // Inno calls it for a yes or no, and the body has to decide something.
        Assert.Equal("BOOLEAN", initialize.ReturnType);
        Assert.NotEmpty(initialize.Body);
    }

    [InstallerFact(SampleInstallers.CloseCombat2)]
    public void RecoversCallsWithTheirArguments()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.CloseCombat2)!);

        var calls = Flatten(package.DecompileScript().Unit).OfType<ScriptCall>().ToList();

        // A call with no arguments would mean the run of pushes before it was not folded in.
        Assert.Contains(calls, call => call.Callee.Equals("ExtractTemporaryFile", StringComparison.OrdinalIgnoreCase)
                                       && call.Arguments.Count == 1);

        Assert.Contains(calls, call => call.Callee.Equals("RegWriteStringValue", StringComparison.OrdinalIgnoreCase)
                                       && call.Arguments.Count == 4);

        // A routine that writes back through a parameter is passed one by reference.
        Assert.Contains(calls, call => call.Callee.Equals("RegQueryStringValue", StringComparison.OrdinalIgnoreCase)
                                       && call.Arguments[3].ByReference);
    }

    [InstallerFact(SampleInstallers.CloseCombat2)]
    public void WritesPascalThatLooksLikeAnInstallerScript()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.CloseCombat2)!);

        var pascal = package.DecompileScript().ToPascal();

        Assert.Contains("function INITIALIZESETUP: BOOLEAN;", pascal);
        Assert.Contains("Result := ", pascal);
        Assert.Contains("EXTRACTTEMPORARYFILE(", pascal);
        Assert.Contains("try", pascal);
    }

    [PowerShellInstallerFact(SampleInstallers.CloseCombat2)]
    public void WritesPowerShellThatPowerShellAccepts()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.CloseCombat2)!);

        var directory = Directory.CreateTempSubdirectory("gog-script-");

        try
        {
            var script = Path.Combine(directory.FullName, "InstallerScript.ps1");
            var module = Path.Combine(directory.FullName, InnoApi.ModuleFileName);

            File.WriteAllText(script, package.DecompileScript().ToPowerShell());
            File.WriteAllText(module, InnoApi.ModuleText());

            Assert.Equal(string.Empty, PowerShell.ParseErrors(script));
            Assert.Equal(string.Empty, PowerShell.ParseErrors(module));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [PowerShellFact]
    public void TheShimModuleIsValidPowerShell()
    {
        var directory = Directory.CreateTempSubdirectory("gog-shim-");

        try
        {
            var module = Path.Combine(directory.FullName, InnoApi.ModuleFileName);

            File.WriteAllText(module, InnoApi.ModuleText());

            Assert.Equal(string.Empty, PowerShell.ParseErrors(module));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static IEnumerable<ScriptExpression> Flatten(ScriptUnit unit) =>
        unit.Routines.SelectMany(routine => Flatten(routine.Body));

    private static IEnumerable<ScriptExpression> Flatten(IEnumerable<ScriptStatement> statements)
    {
        foreach (var statement in statements)
        {
            var expressions = statement switch
            {
                ScriptAssignment assignment => new[] { assignment.Target, assignment.Value },
                ScriptCallStatement call => [call.Call],
                ScriptStep step => [step.Target],
                ScriptIf conditional => [conditional.Condition],
                ScriptWhile loop => [loop.Condition],
                ScriptRepeat loop => [loop.Until],
                ScriptGoto jump when jump.Condition is not null => [jump.Condition],
                _ => [],
            };

            foreach (var expression in expressions.SelectMany(Flatten))
                yield return expression;

            var nested = statement switch
            {
                ScriptIf conditional => conditional.Then.Concat(conditional.Else),
                ScriptWhile loop => loop.Body,
                ScriptRepeat loop => loop.Body,
                ScriptTry guarded => guarded.Body.Concat(guarded.Except).Concat(guarded.Finally),
                _ => [],
            };

            foreach (var expression in Flatten(nested))
                yield return expression;
        }
    }

    private static IEnumerable<ScriptExpression> Flatten(ScriptExpression expression)
    {
        yield return expression;

        var children = expression switch
        {
            ScriptUnary unary => new[] { unary.Operand },
            ScriptBinary binary => [binary.Left, binary.Right],
            ScriptFieldAccess field => [field.Target],
            ScriptElementAccess element => [element.Target, element.Index],
            ScriptCall call => call.Arguments.Select(argument => argument.Value)
                .Concat(call.Instance is null ? [] : new[] { call.Instance }).ToArray(),
            _ => [],
        };

        foreach (var child in children.SelectMany(Flatten))
            yield return child;
    }
}

/// <summary>
/// Runs PowerShell's own parser over a file, which is the only check that settles whether emitted
/// PowerShell is PowerShell.
/// </summary>
public static class PowerShell
{
    /// <summary>The path to <c>pwsh</c>, or <c>null</c> when it is not installed.</summary>
    public static string? Executable { get; } = Locate();

    /// <summary>Parses a file, returning the errors as text or an empty string when there are none.</summary>
    public static string ParseErrors(string path)
    {
        var command =
            "$e = $null; " +
            $"[void][System.Management.Automation.Language.Parser]::ParseFile('{path.Replace("'", "''")}', [ref]$null, [ref]$e); " +
            "$e | ForEach-Object { \"line $($_.Extent.StartLineNumber): $($_.Message)\" }";

        using var process = Process.Start(new ProcessStartInfo(Executable!, ["-NoProfile", "-Command", command])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        var output = process.StandardOutput.ReadToEnd().Trim();

        process.WaitForExit();

        return output;
    }

    private static string? Locate()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var directory in directories)
            foreach (var name in new[] { "pwsh", "pwsh.exe" })
            {
                var candidate = Path.Combine(directory, name);

                if (File.Exists(candidate))
                    return candidate;
            }

        return null;
    }
}

/// <summary>Marks a test that needs PowerShell installed, skipping it when it is not.</summary>
public class PowerShellFactAttribute : FactAttribute
{
    public PowerShellFactAttribute()
    {
        if (PowerShell.Executable is null)
            Skip = "Install PowerShell (pwsh) to check that the emitted script parses.";
    }
}

/// <summary>Marks a test that needs both PowerShell and a real installer.</summary>
public sealed class PowerShellInstallerFactAttribute : PowerShellFactAttribute
{
    public PowerShellInstallerFactAttribute(string fileName)
    {
        Skip ??= InstallerFactAttribute.SkipReason(fileName);
    }
}
