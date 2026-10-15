using LANCommander.GOG.Script.Ifps;

namespace LANCommander.GOG.Script.Analysis;

/// <summary>
/// Works out what each call site puts on the stack.
/// </summary>
/// <remarks>
/// <para>
/// Nearly every function describes itself. The exceptions are Pascal Script's own builtins that
/// take an untyped parameter — <c>Length</c>, <c>SetArrayLength</c>, <c>Inc</c> and the rest. Its
/// compiler builds their packed descriptor from the declaration string and only afterwards adds
/// the untyped parameters, so the descriptor that reaches the container under-reports their arity.
/// Believing it would misread every call to them, so they are corrected here from the declarations
/// in <c>uPSCompiler.pas</c>.
/// </para>
/// <para>
/// When nothing describes a function at all, the arity is recovered from the pops the compiler
/// emits after the call, which unwind exactly the slots it pushed.
/// </para>
/// </remarks>
internal static class CallSignatures
{
    private static readonly IfpsCallSignature TakesOneValueReturns = new(true, [false]);
    private static readonly IfpsCallSignature TakesOneReference = new(false, [true]);
    private static readonly IfpsCallSignature TakesReferenceAndValue = new(false, [true, false]);

    /// <summary>
    /// Pascal Script builtins whose stored descriptor omits their untyped parameters.
    /// </summary>
    private static readonly Dictionary<string, IfpsCallSignature> Builtins = new(StringComparer.Ordinal)
    {
        ["LENGTH"] = TakesOneValueReturns,
        ["GETARRAYLENGTH"] = TakesOneValueReturns,
        ["LOW"] = TakesOneValueReturns,
        ["HIGH"] = TakesOneValueReturns,
        ["INC"] = TakesOneReference,
        ["DEC"] = TakesOneReference,
        ["SETLENGTH"] = TakesReferenceAndValue,
        ["SETARRAYLENGTH"] = TakesReferenceAndValue,
        ["INCLUDE"] = TakesReferenceAndValue,
        ["EXCLUDE"] = TakesReferenceAndValue,
    };

    /// <summary>
    /// Resolves what <paramref name="function"/> consumes, or <c>null</c> when nothing describes it.
    /// </summary>
    public static IfpsCallSignature? Resolve(IfpsFunction function)
    {
        if (function is IfpsExternalFunction { Kind: IfpsImportKind.Internal, Name: { Length: > 0 } name }
            && Builtins.TryGetValue(name, out var builtin))
            return builtin;

        return function.CallSignature;
    }

    /// <summary>
    /// Counts the pops the compiler emits straight after a call, which is how many slots it pushed
    /// for that call — plus, sometimes, a caller-side temporary released in the same run. It is an
    /// upper bound, and only worth using when nothing better exists.
    /// </summary>
    public static int CountTrailingPops(IReadOnlyList<IfpsInstruction> instructions, int callIndex)
    {
        var pops = 0;

        while (callIndex + 1 + pops < instructions.Count
               && instructions[callIndex + 1 + pops].OpCode == IfpsOpCode.Pop)
            pops++;

        return pops;
    }
}
