using LANCommander.GOG.Script;
using LANCommander.GOG.Script.Emit;
using LANCommander.GOG.Script.Ifps;

namespace LANCommander.GOG.Tests;

/// <summary>
/// Reads the compiled Pascal script out of real installers.
/// </summary>
/// <remarks>
/// GOG ships no script source, so there is nothing to compare a decoding against. What can be
/// checked is that the decoding is lossless: rewriting the container must reproduce the original
/// bytes, and every instruction must re-encode to the bytes it was read from. A field read at the
/// wrong width fails both.
/// </remarks>
public class ScriptDecodingTests
{
    public static TheoryData<string> Samples =>
    [
        SampleInstallers.CloseCombat2,
        SampleInstallers.Rayman2,
        SampleInstallers.Il2Sturmovik,
        SampleInstallers.WheelOfTime,
        SampleInstallers.Soldiers,
    ];

    [InstallerTheory]
    [MemberData(nameof(Samples))]
    public void RewritingTheScriptReproducesTheOriginalBytes(string sample)
    {
        if (SampleInstallers.Find(sample) is not { } path)
            return;

        using var package = GogInstaller.Open(path);

        Assert.Empty(package.DecompileScript().VerifyRoundTrip());
    }

    [InstallerTheory]
    [MemberData(nameof(Samples))]
    public void EveryBytecodeFunctionDecodes(string sample)
    {
        if (SampleInstallers.Find(sample) is not { } path)
            return;

        using var package = GogInstaller.Open(path);

        var script = package.DecompileScript().Bytecode;

        Assert.Equal(23u, script.Version);
        Assert.NotNull(script.EntryPoint);
        Assert.True(script.FullyDecoded);

        // A script that decoded to nothing would pass every check above.
        Assert.True(script.ScriptFunctions.Sum(function => function.Instructions.Count) > 10_000);
    }

    [InstallerFact(SampleInstallers.CloseCombat2)]
    public void FindsTheHostFunctionsInnoSetupRegisters()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.CloseCombat2)!);

        var script = package.DecompileScript().Bytecode;
        var imports = script.ExternalFunctions.ToList();

        Assert.Contains(imports, import => import.Name == "EXPANDCONSTANT");
        Assert.Contains(imports, import => import.Name == "EXTRACTTEMPORARYFILE");

        // Native imports name a library and an entry point.
        Assert.Contains(imports, import =>
            import.Kind == IfpsImportKind.Dll && import.NativeImport is { Library: "kernel32.dll" });

        // Class imports name a registered class and one of its methods.
        Assert.Contains(imports, import => import.Kind == IfpsImportKind.Class && import.ClassName is { Length: > 0 });
    }

    [InstallerFact(SampleInstallers.CloseCombat2)]
    public void DisassemblesToPlainText()
    {
        using var package = GogInstaller.Open(SampleInstallers.Find(SampleInstallers.CloseCombat2)!);

        var listing = ScriptDisassembler.Disassemble(package.DecompileScript().Bytecode);

        Assert.Contains("types", listing);
        Assert.Contains("imports", listing);
        Assert.Contains("CALL", listing);

        // A listing carrying raw import descriptors would be a binary file, not something to read.
        Assert.DoesNotContain(listing, character => char.IsControl(character) && character is not ('\n' or '\r'));
    }

    [Fact]
    public void RefusesToDecompileAnInstallerWithNoScript()
    {
        var exception = Assert.Throws<IfpsException>(() => DecompiledScript.Load(ReadOnlyMemory<byte>.Empty));

        Assert.Contains("empty blob", exception.Message);
    }
}
