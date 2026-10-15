using System.Globalization;
using LANCommander.GOG.Script.Ast;
using LANCommander.GOG.Script.Ifps;

namespace LANCommander.GOG.Script.Analysis;

/// <summary>
/// Rebuilds a routine's expressions from its bytecode.
/// </summary>
/// <remarks>
/// <para>
/// The virtual machine names its operands directly, so most instructions map to a statement one for
/// one. What does not is the calling convention: an argument is built by pushing a slot, filling it,
/// and pushing a reference to the caller's result temporary, which only becomes a call expression
/// once the whole run is read together. This class walks the instructions keeping a model of the
/// stack, folds those runs back into calls, and inlines single-use temporaries into the expressions
/// that consume them.
/// </para>
/// <para>
/// A temporary is only given a name of its own when it has to survive — because a routine writes
/// back through it, or because it is read after the call that filled it. Everything else disappears
/// into the expression it feeds.
/// </para>
/// </remarks>
internal sealed class RoutineLifter
{
    private readonly IfpsScript _script;
    private readonly IfpsScriptFunction _function;
    private readonly IfpsCallSignature? _signature;

    private readonly List<LiftedItem> _items = [];
    private readonly List<Slot> _slots = [];
    private readonly List<ScriptVariable> _locals = [];
    private readonly List<ScriptVariable> _temporaries = [];

    private ScriptExpression? _flag;
    private int _localCount;
    private uint _offset;

    private RoutineLifter(IfpsScript script, IfpsScriptFunction function)
    {
        _script = script;
        _function = function;
        _signature = CallSignatures.Resolve(function);
    }

    /// <summary>Rebuilds the expressions of one bytecode routine.</summary>
    public static LiftedRoutine Lift(IfpsScript script, IfpsScriptFunction function)
    {
        var lifter = new RoutineLifter(script, function);

        try
        {
            return lifter.Run();
        }
        catch (Exception exception) when (exception is IfpsException or InvalidOperationException or ArgumentException)
        {
            return new LiftedRoutine([], [], [], exception.Message);
        }
    }

    private LiftedRoutine Run()
    {
        _localCount = CountDeclaredLocals();

        // The slot at depth zero holds the return address the call pushed; nothing ever names it.
        _slots.Add(new Slot { Depth = 0 });

        var body = _function.Instructions;

        for (var index = 0; index < body.Count; index++)
            LiftOne(body, index);

        return new LiftedRoutine(_items, _locals, _temporaries, null);
    }

    /// <summary>
    /// Works out how many of the pushed slots are the routine's own variables rather than working
    /// space for a call.
    /// </summary>
    /// <remarks>
    /// The compiler allocates a routine's variables first and never releases them, so the stack
    /// depth when the routine returns is exactly the number it declared. Taking the smallest depth
    /// across every return keeps an unbalanced path from inflating the count.
    /// </remarks>
    private int CountDeclaredLocals()
    {
        var depth = 0;
        var atReturn = int.MaxValue;

        foreach (var instruction in _function.Instructions)
        {
            switch (instruction.OpCode)
            {
                case IfpsOpCode.Push or IfpsOpCode.PushVar or IfpsOpCode.PushType:
                    depth++;

                    break;

                case IfpsOpCode.Pop or IfpsOpCode.PopAndGoto:
                    depth--;

                    break;

                case IfpsOpCode.PopTwoAndGoto:
                    depth -= 2;

                    break;

                case IfpsOpCode.Return:
                    atReturn = Math.Min(atReturn, depth);

                    break;
            }

            if (depth < 0)
                return 0;
        }

        return atReturn == int.MaxValue ? Math.Max(depth, 0) : atReturn;
    }

    private void LiftOne(IReadOnlyList<IfpsInstruction> body, int index)
    {
        var instruction = body[index];

        _offset = instruction.Offset;

        switch (instruction.OpCode)
        {
            case IfpsOpCode.PushType:
                Push(new Slot { Depth = _slots.Count, TypeIndex = instruction.TypeIndex });

                break;

            case IfpsOpCode.Push or IfpsOpCode.PushVar:
                Push(NewReferenceSlot(instruction.Operands[0]));

                break;

            case IfpsOpCode.Pop:
                PopSlot();

                break;

            case IfpsOpCode.Assign:
                Store(instruction.Operands[0], Read(instruction.Operands[1]));

                break;

            case IfpsOpCode.SetStackPointerToCopy:
                Store(instruction.Operands[0], Read(instruction.Operands[1]));

                break;

            case IfpsOpCode.SetPointer:
                // A pointer slot here is how the compiler reaches a variable indirectly, so the
                // slot is treated as another name for what it points at rather than as an address.
                Store(instruction.Operands[0], Read(instruction.Operands[1]));

                break;

            case IfpsOpCode.Calculate:
                Store(
                    instruction.Operands[0],
                    Combine(
                        Translate(instruction.AluOperator!.Value),
                        Read(instruction.Operands[0]),
                        Read(instruction.Operands[1])));

                break;

            case IfpsOpCode.Compare:
                Store(
                    instruction.Operands[0],
                    Compare(
                        Translate(instruction.CompareOperator!.Value),
                        Read(instruction.Operands[1]),
                        Read(instruction.Operands[2])));

                break;

            case IfpsOpCode.BooleanNot:
                Store(instruction.Operands[0], new ScriptUnary(ScriptUnaryOperator.Not, Read(instruction.Operands[0])));

                break;

            case IfpsOpCode.IntegerNot:
                Store(
                    instruction.Operands[0],
                    new ScriptUnary(ScriptUnaryOperator.BitwiseNot, Read(instruction.Operands[0])));

                break;

            case IfpsOpCode.Negate:
                Store(
                    instruction.Operands[0],
                    new ScriptUnary(ScriptUnaryOperator.Negate, Read(instruction.Operands[0])));

                break;

            case IfpsOpCode.Increment or IfpsOpCode.Decrement:
                Emit(instruction, new ScriptStep(Target(instruction.Operands[0]), instruction.OpCode == IfpsOpCode.Decrement));

                break;

            case IfpsOpCode.Call:
                LiftCall(body, index, instruction);

                break;

            case IfpsOpCode.CallVar:
                LiftIndirectCall(instruction);

                break;

            case IfpsOpCode.SetFlag:
            {
                var value = Read(instruction.Operands[0]);

                _flag = instruction.NegateFlag == true ? new ScriptUnary(ScriptUnaryOperator.Not, value) : value;

                break;
            }

            case IfpsOpCode.FlagGoto:
            {
                var condition = _flag ?? new ScriptVariableReference("JumpFlag");

                _flag = null;

                Branch(instruction, condition);

                break;
            }

            case IfpsOpCode.ConditionalGoto:
                Branch(instruction, Read(instruction.Operands[0]));

                break;

            case IfpsOpCode.ConditionalNotGoto:
                Branch(instruction, new ScriptUnary(ScriptUnaryOperator.Not, Read(instruction.Operands[0])));

                break;

            case IfpsOpCode.Goto:
                _items.Add(new LiftedItem { Offset = instruction.Offset, Flow = new JumpFlow(instruction.Target!.Value) });

                break;

            case IfpsOpCode.PopAndGoto or IfpsOpCode.PopTwoAndGoto:
            {
                // These unwind the loop's working slots on the way out of a break or continue.
                PopSlot();

                if (instruction.OpCode == IfpsOpCode.PopTwoAndGoto)
                    PopSlot();

                _items.Add(new LiftedItem { Offset = instruction.Offset, Flow = new JumpFlow(instruction.Target!.Value) });

                break;
            }

            case IfpsOpCode.Return:
                _items.Add(new LiftedItem { Offset = instruction.Offset, Flow = new ReturnFlow() });

                break;

            case IfpsOpCode.PushExceptionHandler:
                _items.Add(new LiftedItem
                {
                    Offset = instruction.Offset,
                    Flow = new TryStartFlow(instruction.ExceptionRegion!.Value),
                });

                break;

            case IfpsOpCode.PopExceptionHandler:
                _items.Add(new LiftedItem
                {
                    Offset = instruction.Offset,
                    Flow = new TryEndFlow(instruction.HandlerPosition!.Value),
                });

                break;

            case IfpsOpCode.Nop:
                break;

            default:
                Emit(instruction, new ScriptNote($"unhandled opcode {instruction.OpCode}"));

                break;
        }
    }

    private void Branch(IfpsInstruction instruction, ScriptExpression condition) =>
        _items.Add(new LiftedItem
        {
            Offset = instruction.Offset,
            Flow = new BranchFlow(condition, instruction.Target!.Value),
        });

    private void Emit(IfpsInstruction instruction, ScriptStatement statement) =>
        _items.Add(new LiftedItem
        {
            Offset = instruction.Offset,
            Statement = statement,
            Flow = new FallThroughFlow(),
        });

    /// <summary>
    /// Folds the run of pushes that precedes a call back into a call expression.
    /// </summary>
    /// <remarks>
    /// The caller pushes parameters in reverse declaration order, then the object for a method, then
    /// the result slot, so the arguments are read back off the top of the stack in that same order.
    /// </remarks>
    private void LiftCall(IReadOnlyList<IfpsInstruction> body, int index, IfpsInstruction instruction)
    {
        var target = _script.Functions[(int)instruction.FunctionIndex!.Value];
        var signature = CallSignatures.Resolve(target)
                        ?? new IfpsCallSignature(false, new bool[CallSignatures.CountTrailingPops(body, index)]);

        if (signature.SlotCount > _slots.Count - 1)
        {
            Emit(instruction, new ScriptNote(
                $"call to {target.DisplayName} wants {signature.SlotCount} arguments but only " +
                $"{_slots.Count - 1} are on the stack"));

            return;
        }

        var top = _slots.Count - 1;
        var cursor = top;

        var resultSlot = signature.ReturnsValue ? _slots[cursor--] : null;
        var instance = signature.HasImplicitSelf ? Value(_slots[cursor--]) : null;

        var arguments = new List<ScriptArgument>(signature.ParametersByReference.Count);

        foreach (var byReference in signature.ParametersByReference)
        {
            var slot = _slots[cursor--];

            arguments.Add(new ScriptArgument(byReference ? Reference(slot) : Value(slot), byReference));
        }

        // Everything the call consumed has now been accounted for, so nothing in that range is
        // still an unread result waiting to be written out when its slot is released.
        for (var depth = cursor + 1; depth <= top; depth++)
            _slots[depth] = _slots[depth] with { HoldsUnreadCall = false };

        var call = new ScriptCall(CalleeName(target), arguments, KindOf(target), instance);

        if (resultSlot?.Source is { } destination)
            Store(destination, call);
        else
            Emit(instruction, new ScriptCallStatement(call));
    }

    private void LiftIndirectCall(IfpsInstruction instruction)
    {
        // A procedure pointer carries no arity, so the pushed slots cannot be attributed to it.
        var callee = Read(instruction.Operands[0]);

        Emit(instruction, new ScriptCallStatement(
            new ScriptCall(Describe(callee), [], ScriptCallKind.Indirect)));
    }

    private static string Describe(ScriptExpression expression) =>
        expression is ScriptVariableReference reference ? reference.Name : "<procedure pointer>";

    private string CalleeName(IfpsFunction target) =>
        target is IfpsExternalFunction { Kind: IfpsImportKind.Class } method
            ? method.MethodName ?? method.DisplayName
            : target.DisplayName;

    private static ScriptCallKind KindOf(IfpsFunction target) => target switch
    {
        IfpsScriptFunction => ScriptCallKind.Script,
        IfpsExternalFunction { Kind: IfpsImportKind.Class } => ScriptCallKind.Method,
        IfpsExternalFunction { Kind: IfpsImportKind.Dll } => ScriptCallKind.Native,
        _ => ScriptCallKind.Host,
    };

    /// <summary>The value an argument slot carries.</summary>
    private ScriptExpression Value(Slot slot) =>
        slot.Value ?? (slot.Source is { } source ? Read(source) : new ScriptVariableReference(NameOf(slot)));

    /// <summary>
    /// The variable an argument slot points at. The routine being called may write through it, so a
    /// temporary here has to become a real variable rather than stay inlined.
    /// </summary>
    private ScriptExpression Reference(Slot slot)
    {
        if (slot.ReferencedDepth is { } depth)
            return new ScriptVariableReference(Materialize(depth));

        if (slot.Source is { } source)
            return Target(source);

        return Value(slot);
    }

    /// <summary>
    /// Records a pushed argument without resolving it yet.
    /// </summary>
    /// <remarks>
    /// Resolving here would defeat the point: pushing a reference to the caller's result temporary
    /// is what a call looks like, and reading that temporary now would force it into a variable
    /// before the call that fills it has even been seen.
    /// </remarks>
    private Slot NewReferenceSlot(IfpsOperand operand)
    {
        var slot = new Slot { Depth = _slots.Count, Source = operand };

        if (operand is IfpsVariableOperand { Address.IsGlobal: false } variable)
        {
            var depth = variable.Address.StackOffset;

            if (depth > _localCount && depth < _slots.Count)
                slot = slot with { ReferencedDepth = depth };
        }

        return slot;
    }

    private void Push(Slot slot)
    {
        _slots.Add(slot);

        if (slot.Depth >= 1 && slot.Depth <= _localCount && _locals.Count < _localCount)
            _locals.Add(new ScriptVariable(NameOfOffset(slot.Depth), TypeNameOf(slot.TypeIndex)));
    }

    private void PopSlot()
    {
        if (_slots.Count <= 1)
            return;

        var slot = _slots[^1];

        _slots.RemoveAt(_slots.Count - 1);

        if (slot is { HoldsUnreadCall: true, Value: { } discarded })
            _items.Add(new LiftedItem
            {
                Offset = _offset,
                Statement = discarded is ScriptCall call
                    ? new ScriptCallStatement(call)
                    : new ScriptAssignment(new ScriptVariableReference("_"), discarded),
                Flow = new FallThroughFlow(),
            });
    }

    /// <summary>
    /// Stores a value, either by recording it against an unnamed temporary so it can be inlined
    /// later, or by emitting a real assignment.
    /// </summary>
    private void Store(IfpsOperand destination, ScriptExpression value)
    {
        if (destination is IfpsVariableOperand { Address.IsGlobal: false } variable
            && IsInlinableTemporary(variable.Address.StackOffset))
        {
            _slots[variable.Address.StackOffset] = _slots[variable.Address.StackOffset] with
            {
                Value = value,
                HoldsUnreadCall = value is ScriptCall,
            };

            return;
        }

        _items.Add(new LiftedItem
        {
            Offset = _offset,
            Statement = new ScriptAssignment(Target(destination), value),
            Flow = new FallThroughFlow(),
        });
    }

    private void Store(int depth, ScriptExpression value)
    {
        if (IsInlinableTemporary(depth))
        {
            _slots[depth] = _slots[depth] with { Value = value };

            return;
        }

        _items.Add(new LiftedItem
        {
            Offset = _offset,
            Statement = new ScriptAssignment(new ScriptVariableReference(NameOf(_slots[depth])), value),
            Flow = new FallThroughFlow(),
        });
    }

    private bool IsInlinableTemporary(int depth) =>
        depth > _localCount && depth < _slots.Count && _slots[depth].Name is null;

    /// <summary>
    /// Gives a temporary a name of its own, writing out whatever it already held so the value is
    /// not lost.
    /// </summary>
    private string Materialize(int depth)
    {
        if (_slots[depth].Name is { } existing)
            return existing;

        var name = $"Temp{(_temporaries.Count + 1).ToString(CultureInfo.InvariantCulture)}";
        var pending = _slots[depth].Value;

        _temporaries.Add(new ScriptVariable(name, TypeNameOf(_slots[depth].TypeIndex)));
        _slots[depth] = _slots[depth] with { Name = name, Value = null, HoldsUnreadCall = false };

        if (pending is not null)
            _items.Add(new LiftedItem
            {
                Offset = _offset,
                Statement = new ScriptAssignment(new ScriptVariableReference(name), pending),
                Flow = new FallThroughFlow(),
            });

        return name;
    }

    /// <summary>
    /// Reads an operand, inlining a temporary's pending value where there is one.
    /// </summary>
    /// <param name="operand">What to read.</param>
    /// <param name="consume">
    /// False when the read does not count as using the value, so a call still waiting in the
    /// temporary is written out when the slot is released rather than lost.
    /// </param>
    private ScriptExpression Read(IfpsOperand operand, bool consume = true)
    {
        switch (operand)
        {
            case IfpsImmediateOperand immediate:
                return immediate.Value.BaseType == IfpsBaseType.ProcPtr
                    ? RoutineReference(immediate.Value.AsUInt64())
                    : new ScriptLiteral(immediate.Value, IsBooleanType(immediate.TypeIndex));

            case IfpsVariableOperand variable:
                return ReadAddress(variable.Address, consume);

            case IfpsIndexedOperand indexed:
                return Index(indexed.Address, new ScriptLiteral(
                    new IfpsValue { BaseType = IfpsBaseType.S32, Encoded = BitConverter.GetBytes(indexed.Index) },
                    IsBoolean: false),
                    indexed.Index);

            case IfpsVariableIndexedOperand variableIndexed:
                return Index(variableIndexed.Address, ReadAddress(variableIndexed.IndexAddress), null);

            default:
                return new ScriptOpaque(operand.ToString() ?? "?");
        }
    }

    /// <summary>The same as <see cref="Read"/>, but for the left-hand side of an assignment.</summary>
    private ScriptExpression Target(IfpsOperand operand) => Read(operand);

    /// <summary>
    /// Names the routine a procedure-pointer constant refers to.
    /// </summary>
    /// <remarks>
    /// The constant is an index into the function table, and zero is the null pointer.
    /// </remarks>
    private ScriptExpression RoutineReference(ulong index) =>
        new ScriptRoutineReference(
            index == 0 || index >= (ulong)_script.Functions.Count
                ? null
                : _script.Functions[(int)index].DisplayName);

    /// <summary>True when a type index names a Boolean rather than a plain byte.</summary>
    private bool IsBooleanType(uint typeIndex) =>
        typeIndex < (uint)_script.Types.Count
        && _script.Types[(int)typeIndex] is { BaseType: IfpsBaseType.U8 } type
        && type.ExportName is { } name
        && name is "Boolean" or "BOOLEAN" or "ByteBool" or "BYTEBOOL";

    private ScriptExpression ReadAddress(IfpsAddress address, bool consume = true)
    {
        if (address.IsGlobal)
            return new ScriptVariableReference(GlobalName(address.GlobalIndex));

        var depth = address.StackOffset;

        if (depth <= _localCount || depth >= _slots.Count)
            return new ScriptVariableReference(NameOfOffset(depth));

        var slot = _slots[depth];

        if (slot.Value is not { } value)
            return new ScriptVariableReference(Materialize(depth));

        if (slot.HoldsUnreadCall && consume)
            _slots[depth] = slot with { HoldsUnreadCall = false };

        return value;
    }

    /// <summary>
    /// Renders an indexed operand. The same encoding covers a record field and an array element, so
    /// the type table decides which of the two it is.
    /// </summary>
    private ScriptExpression Index(IfpsAddress address, ScriptExpression index, uint? literalIndex)
    {
        var target = ReadAddress(address);
        var type = ResolveType(address);

        return type is IfpsRecordType && literalIndex is { } field
            ? new ScriptFieldAccess(target, $"Field{field.ToString(CultureInfo.InvariantCulture)}")
            : new ScriptElementAccess(target, index);
    }

    private IfpsType? ResolveType(IfpsAddress address)
    {
        uint? typeIndex = null;

        if (address.IsGlobal && address.GlobalIndex < _script.Globals.Count)
            typeIndex = _script.Globals[address.GlobalIndex].TypeIndex;
        else if (!address.IsGlobal && address.StackOffset > 0 && address.StackOffset < _slots.Count)
            typeIndex = _slots[address.StackOffset].TypeIndex;

        return typeIndex is { } index && index < (uint)_script.Types.Count ? _script.Types[(int)index] : null;
    }

    private string NameOf(Slot slot) => slot.Name ?? NameOfOffset(slot.Depth);

    /// <summary>
    /// Names a stack slot.
    /// </summary>
    /// <remarks>
    /// The frame base holds the return address. Below it the caller left the result and then the
    /// parameters; above it the routine allocated its own variables and working space.
    /// </remarks>
    private string NameOfOffset(int offset)
    {
        if (offset == 0)
            return "ReturnAddress";

        if (offset > 0)
            return offset <= _localCount
                ? $"Local{offset.ToString(CultureInfo.InvariantCulture)}"
                : $"Slot{offset.ToString(CultureInfo.InvariantCulture)}";

        var fromTop = -offset;

        if (_signature?.ReturnsValue == true)
            return fromTop == 1 ? "Result" : $"Param{(fromTop - 1).ToString(CultureInfo.InvariantCulture)}";

        return $"Param{fromTop.ToString(CultureInfo.InvariantCulture)}";
    }

    private string GlobalName(int index) =>
        index < _script.Globals.Count ? _script.Globals[index].DisplayName : $"Global{index}";

    private string TypeNameOf(uint? typeIndex) =>
        typeIndex is { } index && index < (uint)_script.Types.Count
            ? _script.Types[(int)index].DisplayName
            : "Variant";

    /// <summary>
    /// Builds a binary expression, dropping the steps that do nothing.
    /// </summary>
    /// <remarks>
    /// Indexing an array whose first element is zero still compiles to a subtraction of zero, and
    /// leaving those in makes every array access read worse for no information gained.
    /// </remarks>
    private static ScriptExpression Combine(ScriptBinaryOperator op, ScriptExpression left, ScriptExpression right)
    {
        if (op is ScriptBinaryOperator.Add or ScriptBinaryOperator.Subtract && IsInteger(right, 0))
            return left;

        if (op is ScriptBinaryOperator.Multiply or ScriptBinaryOperator.Divide && IsInteger(right, 1))
            return left;

        return new ScriptBinary(op, left, right);
    }

    /// <summary>
    /// Builds a comparison, folding away the tests against <c>True</c> and <c>False</c> that a
    /// source-level <c>if X = True</c> compiles to.
    /// </summary>
    private static ScriptExpression Compare(ScriptBinaryOperator op, ScriptExpression left, ScriptExpression right)
    {
        if (op is not (ScriptBinaryOperator.Equal or ScriptBinaryOperator.NotEqual) || !IsBoolean(right))
            return new ScriptBinary(op, left, right);

        var equalsTrue = IsInteger(right, 1) == (op == ScriptBinaryOperator.Equal);

        return equalsTrue ? left : new ScriptUnary(ScriptUnaryOperator.Not, left);
    }

    private static bool IsBoolean(ScriptExpression expression) =>
        expression is ScriptLiteral { Value.BaseType: IfpsBaseType.U8 } literal
        && literal.Value.AsInt64() is 0 or 1;

    private static bool IsInteger(ScriptExpression expression, long value) =>
        expression is ScriptLiteral literal
        && literal.Value.BaseType is not (IfpsBaseType.String or IfpsBaseType.UnicodeString
            or IfpsBaseType.WideString or IfpsBaseType.PChar or IfpsBaseType.Set
            or IfpsBaseType.Single or IfpsBaseType.Double or IfpsBaseType.Extended)
        && literal.Value.AsInt64() == value;

    private static ScriptBinaryOperator Translate(IfpsAluOperator alu) => alu switch
    {
        IfpsAluOperator.Add => ScriptBinaryOperator.Add,
        IfpsAluOperator.Subtract => ScriptBinaryOperator.Subtract,
        IfpsAluOperator.Multiply => ScriptBinaryOperator.Multiply,
        IfpsAluOperator.Divide => ScriptBinaryOperator.Divide,
        IfpsAluOperator.Modulus => ScriptBinaryOperator.Modulus,
        IfpsAluOperator.ShiftLeft => ScriptBinaryOperator.ShiftLeft,
        IfpsAluOperator.ShiftRight => ScriptBinaryOperator.ShiftRight,
        IfpsAluOperator.And => ScriptBinaryOperator.And,
        IfpsAluOperator.Or => ScriptBinaryOperator.Or,
        IfpsAluOperator.Xor => ScriptBinaryOperator.Xor,
        IfpsAluOperator.As => ScriptBinaryOperator.Cast,
        _ => ScriptBinaryOperator.Add,
    };

    private static ScriptBinaryOperator Translate(IfpsCompareOperator compare) => compare switch
    {
        IfpsCompareOperator.GreaterOrEqual => ScriptBinaryOperator.GreaterOrEqual,
        IfpsCompareOperator.LessOrEqual => ScriptBinaryOperator.LessOrEqual,
        IfpsCompareOperator.Greater => ScriptBinaryOperator.Greater,
        IfpsCompareOperator.Less => ScriptBinaryOperator.Less,
        IfpsCompareOperator.NotEqual => ScriptBinaryOperator.NotEqual,
        IfpsCompareOperator.Equal => ScriptBinaryOperator.Equal,
        IfpsCompareOperator.In => ScriptBinaryOperator.In,
        IfpsCompareOperator.Is => ScriptBinaryOperator.Is,
        _ => ScriptBinaryOperator.Equal,
    };

    /// <summary>One entry of the modelled stack.</summary>
    private sealed record Slot
    {
        /// <summary>Where the slot sits relative to the frame base.</summary>
        public required int Depth { get; init; }

        /// <summary>The type the slot was created with, when it was created by a push of a type.</summary>
        public uint? TypeIndex { get; init; }

        /// <summary>What the slot holds, while it is still inlinable.</summary>
        public ScriptExpression? Value { get; init; }

        /// <summary>The name given to the slot once it had to become a real variable.</summary>
        public string? Name { get; init; }

        /// <summary>The slot this one points at, when it was created by pushing a reference.</summary>
        public int? ReferencedDepth { get; init; }

        /// <summary>The operand the slot was pushed from, resolved only when the call is read.</summary>
        public IfpsOperand? Source { get; init; }

        /// <summary>
        /// True when the slot holds a call whose result nothing has read yet.
        /// </summary>
        /// <remarks>
        /// A routine called only for its effect still stores its result into a temporary, which is
        /// then discarded. Inlining that temporary into nothing would delete the call, so the flag
        /// keeps track of it until the slot is either read or released.
        /// </remarks>
        public bool HoldsUnreadCall { get; init; }
    }
}

/// <summary>A routine after its expressions have been rebuilt.</summary>
/// <param name="Items">Its steps, in bytecode order.</param>
/// <param name="Locals">The variables it declared.</param>
/// <param name="Temporaries">The compiler working slots that had to be named.</param>
/// <param name="Error">Why the rebuild failed, or <c>null</c> when it did not.</param>
internal sealed record LiftedRoutine(
    IReadOnlyList<LiftedItem> Items,
    IReadOnlyList<ScriptVariable> Locals,
    IReadOnlyList<ScriptVariable> Temporaries,
    string? Error);
