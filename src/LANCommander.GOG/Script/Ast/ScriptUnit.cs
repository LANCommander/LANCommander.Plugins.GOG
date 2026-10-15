namespace LANCommander.GOG.Script.Ast;

/// <summary>A whole script, recovered from bytecode.</summary>
/// <param name="Globals">The script's global variables.</param>
/// <param name="Routines">The routines it implements, in declaration order.</param>
/// <param name="EntryPoint">The routine the host calls first, or <c>null</c> when there is none.</param>
public sealed record ScriptUnit(
    IReadOnlyList<ScriptVariable> Globals,
    IReadOnlyList<ScriptRoutine> Routines,
    string? EntryPoint)
{
    /// <summary>How many routines came out fully structured, with no unstructured jumps left.</summary>
    public int StructuredRoutineCount => Routines.Count(routine => routine.IsFullyStructured);

    /// <summary>
    /// The share of routines that came out fully structured, between zero and one.
    /// </summary>
    /// <remarks>
    /// This is the decompiler's quality metric. A routine that keeps its jumps is still correct
    /// and complete; it just reads worse, so a drop here is a regression worth looking at.
    /// </remarks>
    public double StructuredFraction =>
        Routines.Count == 0 ? 1 : (double)StructuredRoutineCount / Routines.Count;
}

/// <summary>A routine recovered from bytecode.</summary>
public sealed record ScriptRoutine
{
    /// <summary>The routine's name.</summary>
    public required string Name { get; init; }

    /// <summary>The type of the result, or <c>null</c> when the routine returns nothing.</summary>
    public string? ReturnType { get; init; }

    /// <summary>The routine's parameters, in declaration order.</summary>
    public required IReadOnlyList<ScriptVariable> Parameters { get; init; }

    /// <summary>The routine's local variables, including the temporaries the compiler introduced.</summary>
    public required IReadOnlyList<ScriptVariable> Locals { get; init; }

    /// <summary>The routine's body.</summary>
    public required IReadOnlyList<ScriptStatement> Body { get; init; }

    /// <summary>True when no unstructured jump survived into the body.</summary>
    public required bool IsFullyStructured { get; init; }

    /// <summary>
    /// Why the routine could not be recovered, or <c>null</c> when it was.
    /// </summary>
    /// <remarks>
    /// A routine that fails here still appears in the output, with its note explaining why the
    /// body is missing. Dropping it silently would make the output look complete when it is not.
    /// </remarks>
    public string? Error { get; init; }

    /// <summary>True when the routine returns a value.</summary>
    public bool ReturnsValue => ReturnType is not null;
}

/// <summary>A variable in a recovered script.</summary>
/// <param name="Name">The variable's name.</param>
/// <param name="TypeName">The name of its type.</param>
/// <param name="ByReference">True when a parameter is passed by reference.</param>
public sealed record ScriptVariable(string Name, string TypeName, bool ByReference = false);
