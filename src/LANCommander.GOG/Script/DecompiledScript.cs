using LANCommander.GOG.Script.Analysis;
using LANCommander.GOG.Script.Ast;
using LANCommander.GOG.Script.Emit;
using LANCommander.GOG.Script.Ifps;

namespace LANCommander.GOG.Script;

/// <summary>
/// An installer's compiled Pascal script, decoded and available in readable forms.
/// </summary>
/// <remarks>
/// Decoding is not execution. The bytecode is parsed into instructions and rendered as text;
/// nothing in it is ever run, so the library's promise that opening an installer has no side
/// effects continues to hold.
/// </remarks>
public sealed record DecompiledScript
{
    /// <summary>The decoded container: types, functions, globals, and instructions.</summary>
    public required IfpsScript Bytecode { get; init; }

    /// <summary>The bytes the script was decoded from.</summary>
    public required ReadOnlyMemory<byte> CompiledCode { get; init; }

    /// <summary>
    /// Decodes a compiled script.
    /// </summary>
    /// <param name="compiledCode">The blob from <see cref="Manifest.InstallerScripts.CompiledCode"/>.</param>
    /// <exception cref="IfpsException">The blob is not a compiled script, or is malformed.</exception>
    public static DecompiledScript Load(ReadOnlyMemory<byte> compiledCode) => new()
    {
        Bytecode = IfpsScript.Load(compiledCode),
        CompiledCode = compiledCode,
    };

    /// <summary>The recovered script: globals, routines, and their bodies.</summary>
    /// <remarks>
    /// Recovered once and cached, because both emitters read the same model and structuring a large
    /// script is the expensive half of the work.
    /// </remarks>
    public ScriptUnit Unit => _unit ??= ScriptDecompiler.Decompile(Bytecode);

    private ScriptUnit? _unit;

    /// <summary>Renders the script as an instruction listing.</summary>
    public string ToDisassembly() => ScriptDisassembler.Disassemble(Bytecode);

    /// <summary>Renders the recovered script as Pascal.</summary>
    public string ToPascal() => PascalEmitter.Emit(Unit);

    /// <summary>
    /// Renders the recovered script as PowerShell, calling into the shim module that stands in for
    /// Inno Setup's script API.
    /// </summary>
    /// <remarks>
    /// Write <see cref="Emit.InnoApi.ModuleText"/> beside the result; the script imports it by name.
    /// </remarks>
    public string ToPowerShell() => PowerShellEmitter.Emit(Unit);

    /// <summary>
    /// Re-encodes the script and reports anything that does not reproduce the original bytes.
    /// </summary>
    /// <returns>One message per difference, empty when the script round-trips exactly.</returns>
    public IReadOnlyList<string> VerifyRoundTrip()
    {
        var problems = new List<string>(IfpsWriter.VerifyInstructionEncoding(Bytecode, CompiledCode));
        var rewritten = IfpsWriter.Write(Bytecode);

        if (!rewritten.AsSpan().SequenceEqual(CompiledCode.Span))
            problems.Insert(0, DescribeDifference(CompiledCode.Span, rewritten));

        return problems;
    }

    private static string DescribeDifference(ReadOnlySpan<byte> original, ReadOnlySpan<byte> rewritten)
    {
        if (original.Length != rewritten.Length)
            return $"Rewriting the script produced {rewritten.Length} bytes but it was read from {original.Length}.";

        for (var offset = 0; offset < original.Length; offset++)
            if (original[offset] != rewritten[offset])
                return $"Rewriting the script first differs at offset 0x{offset:X}: " +
                       $"wrote 0x{rewritten[offset]:X2}, read 0x{original[offset]:X2}.";

        return "Rewriting the script produced different bytes.";
    }
}
