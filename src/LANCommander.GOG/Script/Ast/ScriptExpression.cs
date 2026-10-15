using LANCommander.GOG.Script.Ifps;

namespace LANCommander.GOG.Script.Ast;

/// <summary>
/// A value in a recovered routine.
/// </summary>
/// <remarks>
/// The model is deliberately language-neutral: it describes what the bytecode computes, not how
/// any particular language spells it. Each emitter decides the spelling, which is what lets the
/// same recovered routine come out as Pascal or as PowerShell.
/// </remarks>
public abstract record ScriptExpression;

/// <summary>A reference to a parameter, local, global, or temporary.</summary>
/// <param name="Name">The variable's name.</param>
public sealed record ScriptVariableReference(string Name) : ScriptExpression;

/// <summary>A constant written into the bytecode.</summary>
/// <param name="Value">The constant.</param>
/// <param name="IsBoolean">
/// True when the constant's declared type is a Boolean. Pascal Script stores a Boolean and a Byte
/// identically, so without this a message-box flag would read as <c>False</c>.
/// </param>
public sealed record ScriptLiteral(IfpsValue Value, bool IsBoolean = true) : ScriptExpression;

/// <summary>
/// A value this decompiler could not express, carried through verbatim so nothing is silently
/// dropped.
/// </summary>
/// <param name="Text">A description of what the bytecode held.</param>
public sealed record ScriptOpaque(string Text) : ScriptExpression;

/// <summary>
/// A reference to a routine rather than a call of it, which is how a callback is passed.
/// </summary>
/// <param name="Name">The routine's name, or <c>null</c> for a null pointer.</param>
public sealed record ScriptRoutineReference(string? Name) : ScriptExpression;

/// <summary>A field of a record.</summary>
/// <param name="Target">The record.</param>
/// <param name="Name">The field's name.</param>
public sealed record ScriptFieldAccess(ScriptExpression Target, string Name) : ScriptExpression;

/// <summary>An element of an array.</summary>
/// <param name="Target">The array.</param>
/// <param name="Index">The element index.</param>
public sealed record ScriptElementAccess(ScriptExpression Target, ScriptExpression Index) : ScriptExpression;

/// <summary>A one-operand operation.</summary>
/// <param name="Operator">What is applied.</param>
/// <param name="Operand">What it is applied to.</param>
public sealed record ScriptUnary(ScriptUnaryOperator Operator, ScriptExpression Operand) : ScriptExpression;

/// <summary>A two-operand operation.</summary>
/// <param name="Operator">What is applied.</param>
/// <param name="Left">The left operand.</param>
/// <param name="Right">The right operand.</param>
public sealed record ScriptBinary(ScriptBinaryOperator Operator, ScriptExpression Left, ScriptExpression Right)
    : ScriptExpression;

/// <summary>
/// A call to a routine, whether implemented in the script or provided by the installer host.
/// </summary>
/// <param name="Callee">The routine's name.</param>
/// <param name="Arguments">The arguments, in declaration order.</param>
/// <param name="Kind">Where the routine comes from.</param>
/// <param name="Instance">The object a method is called on, or <c>null</c> for a plain routine.</param>
public sealed record ScriptCall(
    string Callee,
    IReadOnlyList<ScriptArgument> Arguments,
    ScriptCallKind Kind,
    ScriptExpression? Instance = null) : ScriptExpression;

/// <summary>One argument of a <see cref="ScriptCall"/>.</summary>
/// <param name="Value">The value passed.</param>
/// <param name="ByReference">True when the routine may write back through this argument.</param>
public sealed record ScriptArgument(ScriptExpression Value, bool ByReference);

/// <summary>Where a called routine is implemented.</summary>
public enum ScriptCallKind
{
    /// <summary>Another routine in the same script.</summary>
    Script,

    /// <summary>A function the installer host registers, which is Inno Setup's script API.</summary>
    Host,

    /// <summary>A method on a host-registered class.</summary>
    Method,

    /// <summary>An entry point in a native library.</summary>
    Native,

    /// <summary>A call through a procedure pointer.</summary>
    Indirect,
}

/// <summary>One-operand operations.</summary>
public enum ScriptUnaryOperator
{
    /// <summary>Logical negation.</summary>
    Not,

    /// <summary>Bitwise complement.</summary>
    BitwiseNot,

    /// <summary>Arithmetic negation.</summary>
    Negate,
}

/// <summary>Two-operand operations.</summary>
public enum ScriptBinaryOperator
{
    /// <summary>Addition, or string concatenation.</summary>
    Add,

    /// <summary>Subtraction.</summary>
    Subtract,

    /// <summary>Multiplication.</summary>
    Multiply,

    /// <summary>Division.</summary>
    Divide,

    /// <summary>Remainder.</summary>
    Modulus,

    /// <summary>Shift left.</summary>
    ShiftLeft,

    /// <summary>Shift right.</summary>
    ShiftRight,

    /// <summary>Bitwise or logical AND.</summary>
    And,

    /// <summary>Bitwise or logical OR.</summary>
    Or,

    /// <summary>Bitwise or logical XOR.</summary>
    Xor,

    /// <summary>Checked cast.</summary>
    Cast,

    /// <summary>Equality.</summary>
    Equal,

    /// <summary>Inequality.</summary>
    NotEqual,

    /// <summary>Less than.</summary>
    Less,

    /// <summary>Less than or equal.</summary>
    LessOrEqual,

    /// <summary>Greater than.</summary>
    Greater,

    /// <summary>Greater than or equal.</summary>
    GreaterOrEqual,

    /// <summary>Set membership.</summary>
    In,

    /// <summary>Type test.</summary>
    Is,
}
