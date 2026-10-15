using System.Globalization;

namespace LANCommander.GOG.Script.Ifps;

/// <summary>
/// Where an instruction operand lives: a global variable, or a slot on the call stack.
/// </summary>
/// <remarks>
/// The virtual machine encodes both in one 32-bit number. Values below
/// <see cref="NegativeStackStart"/> index the global table directly; anything above is a stack
/// address biased by <see cref="StackStart"/>, which makes a function's parameters negative
/// offsets and its locals non-negative ones.
/// </remarks>
/// <param name="Raw">The address exactly as encoded.</param>
public readonly record struct IfpsAddress(uint Raw)
{
    /// <summary>The bias applied to stack addresses.</summary>
    public const uint StackStart = 1610612736;

    /// <summary>The first address that refers to the stack rather than a global.</summary>
    public const uint NegativeStackStart = 1073741824;

    /// <summary>True when the address names a global variable.</summary>
    public bool IsGlobal => Raw < NegativeStackStart;

    /// <summary>The global table index, valid only when <see cref="IsGlobal"/> is true.</summary>
    public int GlobalIndex => (int)Raw;

    /// <summary>
    /// The stack slot relative to the current frame, valid only when <see cref="IsGlobal"/> is
    /// false. Negative values are parameters and the return slot; zero and above are locals.
    /// </summary>
    public int StackOffset => unchecked((int)(Raw - StackStart));

    /// <inheritdoc />
    public override string ToString() =>
        IsGlobal
            ? $"Global{GlobalIndex.ToString(CultureInfo.InvariantCulture)}"
            : StackOffset < 0
                ? $"Arg{(-StackOffset).ToString(CultureInfo.InvariantCulture)}"
                : $"Local{StackOffset.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>An instruction operand.</summary>
public abstract record IfpsOperand;

/// <summary>An operand naming a variable directly.</summary>
/// <param name="Address">Where the variable lives.</param>
public sealed record IfpsVariableOperand(IfpsAddress Address) : IfpsOperand
{
    /// <inheritdoc />
    public override string ToString() => Address.ToString();
}

/// <summary>An operand holding a constant written inline in the bytecode.</summary>
/// <param name="TypeIndex">Index of the constant's type in the script's type table.</param>
/// <param name="Value">The constant.</param>
public sealed record IfpsImmediateOperand(uint TypeIndex, IfpsValue Value) : IfpsOperand
{
    /// <inheritdoc />
    public override string ToString() => Value.ToLiteral();
}

/// <summary>An operand naming a record field or array element at a fixed index.</summary>
/// <param name="Address">The variable being indexed.</param>
/// <param name="Index">The field or element index.</param>
public sealed record IfpsIndexedOperand(IfpsAddress Address, uint Index) : IfpsOperand
{
    /// <inheritdoc />
    public override string ToString() => $"{Address}[{Index.ToString(CultureInfo.InvariantCulture)}]";
}

/// <summary>An operand naming an array element whose index is itself a variable.</summary>
/// <param name="Address">The array being indexed.</param>
/// <param name="IndexAddress">The variable holding the index.</param>
public sealed record IfpsVariableIndexedOperand(IfpsAddress Address, IfpsAddress IndexAddress) : IfpsOperand
{
    /// <inheritdoc />
    public override string ToString() => $"{Address}[{IndexAddress}]";
}

/// <summary>
/// The four boundaries of an exception-handling region, as absolute offsets within the blob.
/// </summary>
/// <remarks>
/// Any of them may be <c>null</c>, which the bytecode spells as <c>0xFFFFFFFF</c>. A
/// <c>try..finally</c> sets <see cref="Finally"/>; a <c>try..except</c> sets
/// <see cref="Except"/>; Pascal's combined form sets both, with
/// <see cref="SecondFinally"/> covering the finally that runs after the except.
/// </remarks>
/// <param name="Finally">Start of the first finally block.</param>
/// <param name="Except">Start of the except block.</param>
/// <param name="SecondFinally">Start of the finally block that follows an except.</param>
/// <param name="End">First offset past the whole region.</param>
public readonly record struct IfpsExceptionRegion(uint? Finally, uint? Except, uint? SecondFinally, uint? End);

/// <summary>
/// One decoded bytecode instruction.
/// </summary>
/// <remarks>
/// Operands vary by opcode, so only the properties an opcode actually uses are populated. The
/// alternative — a record type per instruction — would triple the type count for a format whose
/// instructions are mostly one or two operands wide.
/// </remarks>
public sealed record IfpsInstruction
{
    /// <summary>Absolute offset of the instruction within the compiled blob.</summary>
    public required uint Offset { get; init; }

    /// <summary>Length of the encoded instruction, in bytes.</summary>
    public required int Length { get; init; }

    /// <summary>What the instruction does.</summary>
    public required IfpsOpCode OpCode { get; init; }

    /// <summary>The instruction's variable operands, in the order they are encoded.</summary>
    public IReadOnlyList<IfpsOperand> Operands { get; init; } = [];

    /// <summary>Absolute offset this instruction jumps to, for the branching opcodes.</summary>
    public uint? Target { get; init; }

    /// <summary>Index of the function called, for <see cref="IfpsOpCode.Call"/>.</summary>
    public uint? FunctionIndex { get; init; }

    /// <summary>Index of the type pushed, for <see cref="IfpsOpCode.PushType"/>.</summary>
    public uint? TypeIndex { get; init; }

    /// <summary>The operation applied, for <see cref="IfpsOpCode.Calculate"/>.</summary>
    public IfpsAluOperator? AluOperator { get; init; }

    /// <summary>The comparison applied, for <see cref="IfpsOpCode.Compare"/>.</summary>
    public IfpsCompareOperator? CompareOperator { get; init; }

    /// <summary>True when <see cref="IfpsOpCode.SetFlag"/> inverts the value before storing it.</summary>
    public bool? NegateFlag { get; init; }

    /// <summary>
    /// Which part of an exception region is being closed, for
    /// <see cref="IfpsOpCode.PopExceptionHandler"/>: 0 ends the whole block, 1 the first
    /// finally, 2 the except, and 3 the second finally.
    /// </summary>
    public byte? HandlerPosition { get; init; }

    /// <summary>The region opened, for <see cref="IfpsOpCode.PushExceptionHandler"/>.</summary>
    public IfpsExceptionRegion? ExceptionRegion { get; init; }

    /// <summary>First offset past this instruction.</summary>
    public uint NextOffset => Offset + (uint)Length;
}
