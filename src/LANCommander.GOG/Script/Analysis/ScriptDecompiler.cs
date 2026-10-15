using System.Globalization;
using LANCommander.GOG.Script.Ast;
using LANCommander.GOG.Script.Ifps;

namespace LANCommander.GOG.Script.Analysis;

/// <summary>
/// Recovers a whole script: its globals, and every routine it implements.
/// </summary>
/// <remarks>
/// Names are recovered where the container kept them and invented where it did not. Routine and
/// type names survive compilation because Pascal Script exports them; parameter and local names do
/// not, so they come out positional.
/// </remarks>
public static class ScriptDecompiler
{
    /// <summary>Recovers the script.</summary>
    public static ScriptUnit Decompile(IfpsScript script)
    {
        var globals = script.Globals
            .Select(global => new ScriptVariable(global.DisplayName, TypeName(script, global.TypeIndex)))
            .ToList();

        var routines = script.ScriptFunctions.Select(function => Recover(script, function)).ToList();

        return new ScriptUnit(globals, routines, script.EntryPoint?.DisplayName);
    }

    private static ScriptRoutine Recover(IfpsScript script, IfpsScriptFunction function)
    {
        var signature = function.Signature;
        var parameters = Parameters(script, signature);

        if (function.DecodeError is { } decodeError)
            return Failed(function, parameters, script, signature, decodeError);

        var lifted = RoutineLifter.Lift(script, function);

        if (lifted.Error is { } liftError)
            return Failed(function, parameters, script, signature, liftError);

        var (body, structured) = Structurer.Structure(lifted.Items);

        return new ScriptRoutine
        {
            Name = function.DisplayName,
            ReturnType = ReturnType(script, signature),
            Parameters = parameters,
            Locals = [.. lifted.Locals, .. lifted.Temporaries],
            Body = body,
            IsFullyStructured = structured,
        };
    }

    private static ScriptRoutine Failed(
        IfpsScriptFunction function,
        IReadOnlyList<ScriptVariable> parameters,
        IfpsScript script,
        IfpsSignature? signature,
        string error) =>
        new()
        {
            Name = function.DisplayName,
            ReturnType = ReturnType(script, signature),
            Parameters = parameters,
            Locals = [],
            Body = [new ScriptNote(error)],
            IsFullyStructured = false,
            Error = error,
        };

    private static List<ScriptVariable> Parameters(IfpsScript script, IfpsSignature? signature) =>
        signature is null
            ? []
            : signature.Parameters
                .Select((parameter, index) => new ScriptVariable(
                    $"Param{(index + 1).ToString(CultureInfo.InvariantCulture)}",
                    TypeName(script, parameter.TypeIndex),
                    parameter.ByReference))
                .ToList();

    private static string? ReturnType(IfpsScript script, IfpsSignature? signature) =>
        signature?.ReturnTypeIndex is { } index ? TypeName(script, index) : null;

    private static string TypeName(IfpsScript script, uint index) =>
        index < (uint)script.Types.Count ? script.Types[(int)index].DisplayName : "Variant";
}
