using LANCommander.GOG.Script.Ast;
using LANCommander.GOG.Script.Ifps;

namespace LANCommander.GOG.Script.Analysis;

/// <summary>
/// One step of a routine after its expressions have been rebuilt but before its control flow has
/// been given shape.
/// </summary>
/// <remarks>
/// Splitting the work here keeps the two hard problems apart: rebuilding expressions is a question
/// about the stack, and recovering loops and conditionals is a question about jump targets. Each
/// item still carries the offset it came from, which is what the structurer matches targets against.
/// </remarks>
internal sealed record LiftedItem
{
    /// <summary>Offset of the instruction this came from.</summary>
    public required uint Offset { get; init; }

    /// <summary>What happens here, or <c>null</c> when the item is only a transfer of control.</summary>
    public ScriptStatement? Statement { get; init; }

    /// <summary>Where control goes next.</summary>
    public required LiftedFlow Flow { get; init; }
}

/// <summary>Where control goes after a <see cref="LiftedItem"/>.</summary>
internal abstract record LiftedFlow;

/// <summary>Control continues with the next item.</summary>
internal sealed record FallThroughFlow : LiftedFlow;

/// <summary>Control always moves to another offset.</summary>
/// <param name="Target">Where it goes.</param>
internal sealed record JumpFlow(uint Target) : LiftedFlow;

/// <summary>Control moves to another offset when a condition holds, and continues otherwise.</summary>
/// <param name="Condition">What is tested.</param>
/// <param name="Target">Where it goes when the test passes.</param>
internal sealed record BranchFlow(ScriptExpression Condition, uint Target) : LiftedFlow;

/// <summary>Control leaves the routine.</summary>
internal sealed record ReturnFlow : LiftedFlow;

/// <summary>An exception-handling region opens here.</summary>
/// <param name="Region">Its four boundaries.</param>
internal sealed record TryStartFlow(IfpsExceptionRegion Region) : LiftedFlow;

/// <summary>One part of an exception-handling region closes here.</summary>
/// <param name="Position">Which part: 0 the block, 1 the finally, 2 the except, 3 the second finally.</param>
internal sealed record TryEndFlow(byte Position) : LiftedFlow;
