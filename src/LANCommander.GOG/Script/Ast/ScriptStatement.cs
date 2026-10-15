namespace LANCommander.GOG.Script.Ast;

/// <summary>An action in a recovered routine.</summary>
public abstract record ScriptStatement;

/// <summary>Stores a value.</summary>
/// <param name="Target">Where the value goes.</param>
/// <param name="Value">The value.</param>
public sealed record ScriptAssignment(ScriptExpression Target, ScriptExpression Value) : ScriptStatement;

/// <summary>Calls a routine and discards anything it returns.</summary>
/// <param name="Call">The call.</param>
public sealed record ScriptCallStatement(ScriptCall Call) : ScriptStatement;

/// <summary>Adds one to, or subtracts one from, a variable.</summary>
/// <param name="Target">The variable.</param>
/// <param name="Decrement">True to subtract rather than add.</param>
public sealed record ScriptStep(ScriptExpression Target, bool Decrement) : ScriptStatement;

/// <summary>Chooses between two branches.</summary>
/// <param name="Condition">What is tested.</param>
/// <param name="Then">Run when the condition holds.</param>
/// <param name="Else">Run when it does not. Empty when there is no else branch.</param>
public sealed record ScriptIf(
    ScriptExpression Condition,
    IReadOnlyList<ScriptStatement> Then,
    IReadOnlyList<ScriptStatement> Else) : ScriptStatement;

/// <summary>Repeats while a condition holds, testing before each pass.</summary>
/// <param name="Condition">What is tested.</param>
/// <param name="Body">What is repeated.</param>
public sealed record ScriptWhile(ScriptExpression Condition, IReadOnlyList<ScriptStatement> Body) : ScriptStatement;

/// <summary>Repeats until a condition holds, testing after each pass.</summary>
/// <param name="Body">What is repeated.</param>
/// <param name="Until">What is tested.</param>
public sealed record ScriptRepeat(IReadOnlyList<ScriptStatement> Body, ScriptExpression Until) : ScriptStatement;

/// <summary>Leaves the innermost loop.</summary>
public sealed record ScriptBreak : ScriptStatement;

/// <summary>Starts the next pass of the innermost loop.</summary>
public sealed record ScriptContinue : ScriptStatement;

/// <summary>Returns from the routine.</summary>
public sealed record ScriptExit : ScriptStatement;

/// <summary>Guards a block with exception and cleanup handlers.</summary>
/// <param name="Body">The guarded code.</param>
/// <param name="Except">Runs when the body raises. Empty when the block has no handler.</param>
/// <param name="Finally">Runs on the way out either way. Empty when the block has no cleanup.</param>
public sealed record ScriptTry(
    IReadOnlyList<ScriptStatement> Body,
    IReadOnlyList<ScriptStatement> Except,
    IReadOnlyList<ScriptStatement> Finally) : ScriptStatement;

/// <summary>
/// A branch target, emitted only where the control flow could not be expressed structurally.
/// </summary>
/// <param name="Name">The label's name.</param>
public sealed record ScriptLabel(string Name) : ScriptStatement;

/// <summary>
/// An unstructured jump.
/// </summary>
/// <remarks>
/// The structurer emits these rather than failing when a region does not reduce to loops and
/// conditionals. Output stays complete and correct; it is only less pleasant to read.
/// </remarks>
/// <param name="Target">The label jumped to.</param>
/// <param name="Condition">What must hold for the jump to happen, or <c>null</c> when it always does.</param>
public sealed record ScriptGoto(string Target, ScriptExpression? Condition = null) : ScriptStatement;

/// <summary>A note from the decompiler, carried into the output as a comment.</summary>
/// <param name="Text">What the note says.</param>
public sealed record ScriptNote(string Text) : ScriptStatement;
