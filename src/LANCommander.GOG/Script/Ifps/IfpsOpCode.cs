namespace LANCommander.GOG.Script.Ifps;

/// <summary>
/// The instruction set of the Pascal Script virtual machine.
/// </summary>
/// <remarks>
/// These are the <c>CM_*</c> constants from <c>uPSUtils.pas</c>. The machine is not a pure
/// stack machine: most instructions name their operands directly as global or stack slots,
/// and the stack is used only to pass arguments to calls.
/// </remarks>
public enum IfpsOpCode : byte
{
    /// <summary>Copies the source operand into the destination operand.</summary>
    Assign = 0,

    /// <summary>Applies an <see cref="IfpsAluOperator"/> to the destination in place.</summary>
    Calculate = 1,

    /// <summary>Pushes a copy of the operand onto the stack.</summary>
    Push = 2,

    /// <summary>Pushes a reference to the operand onto the stack, for a <c>var</c> parameter.</summary>
    PushVar = 3,

    /// <summary>Discards the top of the stack.</summary>
    Pop = 4,

    /// <summary>Calls the function at the given index.</summary>
    Call = 5,

    /// <summary>Jumps unconditionally.</summary>
    Goto = 6,

    /// <summary>Jumps when the operand is non-zero.</summary>
    ConditionalGoto = 7,

    /// <summary>Jumps when the operand is zero.</summary>
    ConditionalNotGoto = 8,

    /// <summary>Returns from the current function.</summary>
    Return = 9,

    /// <summary>Sets the type of the top of the stack. Never emitted by the compiler.</summary>
    SetStackType = 10,

    /// <summary>Pushes a freshly created value of the given type onto the stack.</summary>
    PushType = 11,

    /// <summary>Applies an <see cref="IfpsCompareOperator"/> to two operands, storing a boolean.</summary>
    Compare = 12,

    /// <summary>Calls through a procedure pointer held in the operand.</summary>
    CallVar = 13,

    /// <summary>Points the destination pointer at the source operand.</summary>
    SetPointer = 14,

    /// <summary>Logically negates the operand in place.</summary>
    BooleanNot = 15,

    /// <summary>Arithmetically negates the operand in place.</summary>
    Negate = 16,

    /// <summary>Sets the jump flag from the operand, optionally inverted.</summary>
    SetFlag = 17,

    /// <summary>Jumps when the jump flag is set.</summary>
    FlagGoto = 18,

    /// <summary>Opens an exception-handling region, naming its four boundaries.</summary>
    PushExceptionHandler = 19,

    /// <summary>Closes one part of an exception-handling region.</summary>
    PopExceptionHandler = 20,

    /// <summary>Bitwise-complements the operand in place.</summary>
    IntegerNot = 21,

    /// <summary>
    /// Points the destination pointer at a freshly made copy of the source, which is how the
    /// compiler passes open arrays and <c>array of const</c> arguments.
    /// </summary>
    SetStackPointerToCopy = 22,

    /// <summary>Increments the operand in place.</summary>
    Increment = 23,

    /// <summary>Decrements the operand in place.</summary>
    Decrement = 24,

    /// <summary>Discards the top of the stack, then jumps.</summary>
    PopAndGoto = 25,

    /// <summary>Discards the top two stack entries, then jumps.</summary>
    PopTwoAndGoto = 26,

    /// <summary>Does nothing.</summary>
    Nop = 255,
}

/// <summary>The arithmetic and bitwise operations <see cref="IfpsOpCode.Calculate"/> can apply.</summary>
public enum IfpsAluOperator : byte
{
    /// <summary>Addition, or string concatenation.</summary>
    Add = 0,

    /// <summary>Subtraction.</summary>
    Subtract = 1,

    /// <summary>Multiplication.</summary>
    Multiply = 2,

    /// <summary>Division.</summary>
    Divide = 3,

    /// <summary>Remainder.</summary>
    Modulus = 4,

    /// <summary>Shift left.</summary>
    ShiftLeft = 5,

    /// <summary>Shift right.</summary>
    ShiftRight = 6,

    /// <summary>Bitwise or logical AND.</summary>
    And = 7,

    /// <summary>Bitwise or logical OR.</summary>
    Or = 8,

    /// <summary>Bitwise or logical XOR.</summary>
    Xor = 9,

    /// <summary>Checked cast, Pascal's <c>as</c>.</summary>
    As = 10,
}

/// <summary>The comparisons <see cref="IfpsOpCode.Compare"/> can apply.</summary>
public enum IfpsCompareOperator : byte
{
    /// <summary>Greater than or equal.</summary>
    GreaterOrEqual = 0,

    /// <summary>Less than or equal.</summary>
    LessOrEqual = 1,

    /// <summary>Greater than.</summary>
    Greater = 2,

    /// <summary>Less than.</summary>
    Less = 3,

    /// <summary>Not equal.</summary>
    NotEqual = 4,

    /// <summary>Equal.</summary>
    Equal = 5,

    /// <summary>Set membership, Pascal's <c>in</c>.</summary>
    In = 6,

    /// <summary>Type test, Pascal's <c>is</c>.</summary>
    Is = 7,
}
