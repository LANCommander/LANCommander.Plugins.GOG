using System.Globalization;
using LANCommander.GOG.Script.Ast;

namespace LANCommander.GOG.Script.Analysis;

/// <summary>
/// Turns a routine's jumps back into loops, conditionals, and guarded blocks.
/// </summary>
/// <remarks>
/// <para>
/// The bytecode was produced by a compiler working from structured Pascal, so its control flow is
/// reducible and the shapes it can take are few. That makes pattern matching over the linear
/// instruction order both simpler and more faithful than a general graph algorithm: a backward
/// branch is a loop, a forward conditional branch is an <c>if</c>, and the exception opcodes carry
/// their own boundaries.
/// </para>
/// <para>
/// Anything that does not fit becomes a label and a jump rather than a failure. Output is always
/// complete; when a region defeats the structurer the routine is reported as not fully structured
/// so the loss of readability is visible instead of silent.
/// </para>
/// </remarks>
internal sealed class Structurer
{
    private readonly IReadOnlyList<LiftedItem> _items;
    private readonly int?[] _targets;
    private readonly HashSet<int> _labels = [];

    private Structurer(IReadOnlyList<LiftedItem> items)
    {
        _items = items;
        _targets = new int?[items.Count];

        for (var index = 0; index < items.Count; index++)
            _targets[index] = items[index].Flow switch
            {
                JumpFlow jump => IndexOf(jump.Target),
                BranchFlow branch => IndexOf(branch.Target),
                _ => null,
            };
    }

    /// <summary>Recovers the structure of one routine.</summary>
    /// <returns>The body, and whether it came out without any jumps left in it.</returns>
    public static (IReadOnlyList<ScriptStatement> Body, bool FullyStructured) Structure(IReadOnlyList<LiftedItem> items)
    {
        var structurer = new Structurer(items);

        // Labels are only known once the jumps that need them have been found, so the first pass
        // discovers them and the second places them. A third pass cannot find anything new, because
        // nothing about the decision to emit a jump depends on where labels went.
        var body = structurer.Region(0, items.Count, null);

        if (structurer._labels.Count == 0)
            return (body, true);

        return (structurer.Region(0, items.Count, null), false);
    }

    /// <summary>
    /// Recovers a range of items, taking whichever construct opens first.
    /// </summary>
    /// <remarks>
    /// Loops and guarded blocks nest properly, so the one that starts earlier is the one that
    /// encloses the other. Splitting on a loop first would cut a guarded block in half whenever the
    /// loop sits inside it, which is the usual shape of a <c>try..finally</c> around an enumeration.
    /// </remarks>
    private List<ScriptStatement> Region(int low, int high, LoopContext? loop)
    {
        if (low >= high)
            return [];

        var edge = FindBackEdge(low, high);
        var guard = FindGuardedBlock(low, high);

        if (guard is { } block && (edge is not { } first || block.Start <= first.Header))
        {
            var statements = Region(low, block.Start, loop);

            statements.Add(Guarded(block, loop));
            statements.AddRange(Region(block.End, high, loop));

            return statements;
        }

        if (edge is not { } backEdge)
            return Linear(low, high, loop);

        var body = Region(low, backEdge.Header, loop);

        body.Add(Loop(backEdge.Header, backEdge.Tail, loop));
        body.AddRange(Region(backEdge.Tail + 1, high, loop));

        return body;
    }

    /// <summary>
    /// Finds the first guarded block in a range whose boundaries fall inside it.
    /// </summary>
    private GuardedBlock? FindGuardedBlock(int low, int high)
    {
        for (var index = low; index < high; index++)
        {
            if (_items[index].Flow is not TryStartFlow start)
                continue;

            var region = start.Region;
            var handlerOffset = region.Except ?? region.Finally;

            if (handlerOffset is not { } handler
                || region.End is not { } endOffset
                || IndexOf(handler) is not { } handlerIndex
                || IndexOf(endOffset) is not { } endIndex
                || handlerIndex <= index
                || endIndex < handlerIndex
                || endIndex > high)
                continue;

            return new GuardedBlock(index, handlerIndex, endIndex, region.Except is not null);
        }

        return null;
    }

    /// <summary>
    /// Finds the outermost loop in a range: the earliest item something inside the range branches
    /// back to, paired with the last branch that does so.
    /// </summary>
    private (int Header, int Tail)? FindBackEdge(int low, int high)
    {
        var header = int.MaxValue;
        var tail = -1;

        for (var index = low; index < high; index++)
        {
            if (_targets[index] is not { } target || target < low || target > index)
                continue;

            if (target < header)
            {
                header = target;
                tail = index;
            }
            else if (target == header)
            {
                tail = index;
            }
        }

        return tail < 0 ? null : (header, tail);
    }

    /// <summary>
    /// Recovers one loop. A conditional branch at the head that leaves the loop is a <c>while</c>;
    /// a conditional branch at the tail that re-enters it is a <c>repeat</c>; anything else is a
    /// loop with its exits inside the body.
    /// </summary>
    private ScriptStatement Loop(int header, int tail, LoopContext? enclosing)
    {
        var exit = tail + 1;

        if (_items[header].Flow is BranchFlow head && _targets[header] == exit)
            return new ScriptWhile(
                Negate(head.Condition),
                Region(header + 1, tail, new LoopContext(header, header, exit, enclosing)));

        // A repeat loop runs its test at the tail, so that is also where a continue lands.
        if (_items[tail].Flow is BranchFlow back && _targets[tail] == header)
            return new ScriptRepeat(
                Region(header, tail, new LoopContext(header, tail, exit, enclosing)),
                Negate(back.Condition));

        return new ScriptWhile(TrueLiteral, Region(header, tail, new LoopContext(header, header, exit, enclosing)));
    }

    /// <summary>Recovers a range that contains no loops.</summary>
    private List<ScriptStatement> Linear(int low, int high, LoopContext? loop)
    {
        var statements = new List<ScriptStatement>();
        var index = low;

        while (index < high)
        {
            if (_labels.Contains(index))
                statements.Add(new ScriptLabel(LabelFor(index)));

            var item = _items[index];

            if (item.Statement is { } statement)
                statements.Add(statement);

            switch (item.Flow)
            {
                case FallThroughFlow:
                    index++;

                    break;

                case ReturnFlow:
                    // A return at the very end of a routine is where it would end anyway.
                    if (index != _items.Count - 1)
                        statements.Add(new ScriptExit());

                    index++;

                    break;

                case TryStartFlow:
                    // A guarded block whose boundaries did not fall inside this range; its body is
                    // still emitted, it just loses the shape of the block around it.
                    statements.Add(new ScriptNote("a guarded block could not be delimited"));
                    index++;

                    break;

                case TryEndFlow:
                    index++;

                    break;

                case JumpFlow:
                    index = Unconditional(index, high, loop, statements);

                    break;

                case BranchFlow branch:
                    index = Conditional(index, high, loop, branch, statements);

                    break;

                default:
                    index++;

                    break;
            }
        }

        return statements;
    }

    private int Unconditional(int index, int high, LoopContext? loop, List<ScriptStatement> statements)
    {
        var target = _targets[index];

        if (loop?.Exit == target)
            statements.Add(new ScriptBreak());
        else if (loop?.ContinueAt == target)
            statements.Add(new ScriptContinue());
        else if (IsRoutineEnd(target))
            statements.Add(new ScriptExit());
        else if (target != high && target is { } destination)
            statements.Add(new ScriptGoto(Mark(destination)));

        return index + 1;
    }

    /// <summary>
    /// Recovers a conditional branch. A forward branch guards the code it skips, and the jump that
    /// ends that code — when it clears the branch target — is what makes it an <c>else</c>.
    /// </summary>
    private int Conditional(int index, int high, LoopContext? loop, BranchFlow branch, List<ScriptStatement> statements)
    {
        var target = _targets[index];

        if (target is not { } forward || forward <= index || forward > high)
        {
            statements.Add(LeaveOrJump(target, loop, branch.Condition));

            return index + 1;
        }

        var last = forward - 1;

        if (last > index && _items[last].Flow is JumpFlow && _targets[last] is { } join && join > forward && join <= high)
        {
            var whenTrue = Region(index + 1, last, loop);
            var whenFalse = Region(forward, join, loop);

            Add(statements, whenTrue.Count == 0
                ? new ScriptIf(branch.Condition, whenFalse, [])
                : new ScriptIf(Negate(branch.Condition), whenTrue, whenFalse));

            return join;
        }

        Add(statements, new ScriptIf(Negate(branch.Condition), Region(index + 1, forward, loop), []));

        return forward;
    }

    /// <summary>
    /// Adds a conditional, unless it is one of the empty ones Pascal Script emits for short-circuit
    /// <c>and</c> and <c>or</c>.
    /// </summary>
    /// <remarks>
    /// Those branches skip the evaluation of the second operand, which the expression rebuilder has
    /// already folded into the condition that consumes it, so nothing is left for them to guard —
    /// and anything the condition calls survives in that folded expression.
    /// </remarks>
    private static void Add(List<ScriptStatement> statements, ScriptIf conditional)
    {
        if (conditional.Then.Count == 0 && conditional.Else.Count == 0)
            return;

        statements.Add(conditional);
    }

    private ScriptStatement LeaveOrJump(int? target, LoopContext? loop, ScriptExpression condition) =>
        loop?.Exit == target ? new ScriptIf(condition, [new ScriptBreak()], [])
        : loop?.ContinueAt == target ? new ScriptIf(condition, [new ScriptContinue()], [])
        : IsRoutineEnd(target) ? new ScriptIf(condition, [new ScriptExit()], [])
        : target is { } destination ? new ScriptGoto(Mark(destination), condition)
        : new ScriptNote("branch to an offset outside the routine");

    /// <summary>
    /// True when a jump lands where the routine ends anyway, which is an <c>Exit</c> rather than a
    /// jump that needs a label.
    /// </summary>
    private bool IsRoutineEnd(int? target) =>
        target is { } index
        && (index == _items.Count
            || (index == _items.Count - 1 && _items[index].Flow is ReturnFlow && _items[index].Statement is null));

    /// <summary>
    /// Recovers a guarded block. The opcode that opens it names where its handler and its end are,
    /// so the ranges need no searching; the opcodes that close each part are markers and produce
    /// nothing.
    /// </summary>
    private ScriptStatement Guarded(GuardedBlock block, LoopContext? loop)
    {
        var body = Region(block.Start + 1, block.Handler, loop);
        var recovery = Region(block.Handler, block.End, loop);

        if (!block.CatchesExceptions)
            return new ScriptTry(body, [], recovery);

        // An empty handler is a script deliberately swallowing the error, which is worth saying
        // rather than leaving as a block that looks like it lost its contents.
        return new ScriptTry(
            body,
            recovery.Count > 0 ? recovery : [new ScriptNote("any error here is ignored")],
            []);
    }

    /// <summary>Records that an item needs a label, so the next pass can place one before it.</summary>
    private string Mark(int index)
    {
        _labels.Add(index);

        return LabelFor(index);
    }

    private string LabelFor(int index) =>
        $"L{_items[index].Offset.ToString("X", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The first item at or after an offset, which is where a branch to it lands.
    /// </summary>
    /// <remarks>
    /// Items are in bytecode order, so this is a binary search. A routine with a few thousand
    /// instructions has a few thousand branch targets, and scanning for each of them would make
    /// resolving them quadratic in the size of the routine.
    /// </remarks>
    private int? IndexOf(uint offset)
    {
        var low = 0;
        var high = _items.Count;

        while (low < high)
        {
            var middle = low + ((high - low) / 2);

            if (_items[middle].Offset >= offset)
                high = middle;
            else
                low = middle + 1;
        }

        return low;
    }

    private static ScriptExpression Negate(ScriptExpression condition) =>
        condition is ScriptUnary { Operator: ScriptUnaryOperator.Not } negated
            ? negated.Operand
            : new ScriptUnary(ScriptUnaryOperator.Not, condition);

    private static ScriptExpression TrueLiteral =>
        new ScriptLiteral(new Ifps.IfpsValue { BaseType = Ifps.IfpsBaseType.U8, Encoded = new byte[] { 1 } });

    /// <summary>The loop a range sits inside, so a jump out of it can become a break or a continue.</summary>
    /// <param name="Header">Where the loop starts.</param>
    /// <param name="ContinueAt">Where a continue lands: the head of a while, the test of a repeat.</param>
    /// <param name="Exit">The first item after the loop.</param>
    /// <param name="Enclosing">The loop around this one, if any.</param>
    private sealed record LoopContext(int Header, int ContinueAt, int Exit, LoopContext? Enclosing);

    /// <summary>A guarded block, once its boundaries have been resolved to item indices.</summary>
    /// <param name="Start">The item that opens the block.</param>
    /// <param name="Handler">The first item of the handler.</param>
    /// <param name="End">The first item past the whole block.</param>
    /// <param name="CatchesExceptions">True for a <c>try..except</c>, false for a <c>try..finally</c>.</param>
    private sealed record GuardedBlock(int Start, int Handler, int End, bool CatchesExceptions);
}
