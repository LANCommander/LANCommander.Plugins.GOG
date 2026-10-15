using LANCommander.GOG.Script.Analysis;
using LANCommander.GOG.Script.Ast;
using LANCommander.GOG.Script.Ifps;

namespace LANCommander.GOG.Tests;

/// <summary>
/// Covers the recovery of control flow, driven by hand-built items so each shape can be stated
/// exactly rather than hunted for inside a real script.
/// </summary>
public class StructurerTests
{
    [Fact]
    public void RecoversAConditional()
    {
        // if X then Y
        var (body, structured) = Structurer.Structure([
            Branch(0, "NotX", target: 20),
            Step(10, "Y"),
            Step(20, "Z"),
        ]);

        Assert.True(structured);

        var conditional = Assert.IsType<ScriptIf>(body[0]);

        // The branch leaves when the condition fails, so the recovered test is its negation.
        Assert.Equal("X", Assert.IsType<ScriptVariableReference>(conditional.Condition).Name);
        Assert.Single(conditional.Then);
        Assert.Empty(conditional.Else);

        Assert.Equal(2, body.Count);
    }

    [Fact]
    public void RecoversAConditionalWithBothBranches()
    {
        // if X then Y else Z
        var (body, structured) = Structurer.Structure([
            Branch(0, "NotX", target: 30),
            Step(10, "Y"),
            Jump(20, target: 40),
            Step(30, "Z"),
            Step(40, "After"),
        ]);

        Assert.True(structured);

        var conditional = Assert.IsType<ScriptIf>(body[0]);

        Assert.Single(conditional.Then);
        Assert.Single(conditional.Else);
    }

    [Fact]
    public void RecoversAWhileLoop()
    {
        // while X do Y
        var (body, structured) = Structurer.Structure([
            Branch(0, "NotX", target: 30),
            Step(10, "Y"),
            Jump(20, target: 0),
            Step(30, "After"),
        ]);

        Assert.True(structured);

        var loop = Assert.IsType<ScriptWhile>(body[0]);

        Assert.Equal("X", Assert.IsType<ScriptVariableReference>(loop.Condition).Name);
        Assert.Single(loop.Body);
    }

    [Fact]
    public void RecoversARepeatLoop()
    {
        // repeat Y until X
        var (body, structured) = Structurer.Structure([
            Step(0, "Y"),
            Branch(10, "NotX", target: 0),
            Step(20, "After"),
        ]);

        Assert.True(structured);

        var loop = Assert.IsType<ScriptRepeat>(body[0]);

        Assert.Single(loop.Body);
        Assert.Equal("X", Assert.IsType<ScriptVariableReference>(loop.Until).Name);
    }

    [Fact]
    public void TurnsJumpsOutOfALoopIntoBreakAndContinue()
    {
        var (body, structured) = Structurer.Structure([
            Branch(0, "NotX", target: 50),   // while X do
            Jump(10, target: 50),            //   Break
            Jump(20, target: 0),             //   Continue
            Step(30, "Y"),
            Jump(40, target: 0),             // loop back
            Step(50, "After"),
        ]);

        Assert.True(structured);

        var loop = Assert.IsType<ScriptWhile>(body[0]);

        Assert.IsType<ScriptBreak>(loop.Body[0]);
        Assert.IsType<ScriptContinue>(loop.Body[1]);
    }

    [Fact]
    public void RecoversAGuardedBlockWithCleanup()
    {
        var region = new IfpsExceptionRegion(Finally: 20, Except: null, SecondFinally: null, End: 40);

        var (body, structured) = Structurer.Structure([
            new LiftedItem { Offset = 0, Flow = new TryStartFlow(region) },
            Step(10, "Work"),
            Step(20, "Cleanup"),
            Step(40, "After"),
        ]);

        Assert.True(structured);

        var guarded = Assert.IsType<ScriptTry>(body[0]);

        Assert.Single(guarded.Body);
        Assert.Empty(guarded.Except);
        Assert.Single(guarded.Finally);
    }

    [Fact]
    public void RecoversAGuardedBlockThatSwallowsErrors()
    {
        var region = new IfpsExceptionRegion(Finally: null, Except: 20, SecondFinally: null, End: 30);

        var (body, _) = Structurer.Structure([
            new LiftedItem { Offset = 0, Flow = new TryStartFlow(region) },
            Step(10, "Work"),
            new LiftedItem { Offset = 20, Flow = new TryEndFlow(2) },
            Step(30, "After"),
        ]);

        var guarded = Assert.IsType<ScriptTry>(body[0]);

        Assert.Single(guarded.Body);

        // An empty handler is the script ignoring the error, which is worth saying out loud.
        Assert.IsType<ScriptNote>(Assert.Single(guarded.Except));
    }

    [Fact]
    public void KeepsAJumpItCannotExpressRatherThanFailing()
    {
        // A branch from outside a loop into the middle of its body gives the loop two entry
        // points, which is the shape no arrangement of loops and conditionals can express.
        var (body, structured) = Structurer.Structure([
            Step(0, "A"),
            Branch(10, "NotX", target: 30),
            Step(20, "B"),
            Step(30, "C"),
            Branch(40, "NotY", target: 20),
            Step(50, "D"),
        ]);

        Assert.False(structured);

        // Nothing is dropped: the jump becomes a label and a goto.
        Assert.Contains(Flatten(body), statement => statement is ScriptGoto);
        Assert.Contains(Flatten(body), statement => statement is ScriptLabel);
    }

    [Fact]
    public void TurnsAJumpToTheEndIntoAnExit()
    {
        var (body, structured) = Structurer.Structure([
            Branch(0, "NotX", target: 30),
            Step(10, "Y"),
            Jump(20, target: 30),
            new LiftedItem { Offset = 30, Flow = new ReturnFlow() },
        ]);

        Assert.True(structured);
        Assert.DoesNotContain(Flatten(body), statement => statement is ScriptGoto);
    }

    private static IEnumerable<ScriptStatement> Flatten(IEnumerable<ScriptStatement> statements)
    {
        foreach (var statement in statements)
        {
            yield return statement;

            var nested = statement switch
            {
                ScriptIf conditional => conditional.Then.Concat(conditional.Else),
                ScriptWhile loop => loop.Body,
                ScriptRepeat loop => loop.Body,
                ScriptTry guarded => guarded.Body.Concat(guarded.Except).Concat(guarded.Finally),
                _ => [],
            };

            foreach (var child in Flatten(nested))
                yield return child;
        }
    }

    private static LiftedItem Step(uint offset, string name) => new()
    {
        Offset = offset,
        Statement = new ScriptCallStatement(new ScriptCall(name, [], ScriptCallKind.Script)),
        Flow = new FallThroughFlow(),
    };

    private static LiftedItem Jump(uint offset, uint target) =>
        new() { Offset = offset, Flow = new JumpFlow(target) };

    /// <summary>A branch taken when <paramref name="condition"/> holds, spelled as a negation.</summary>
    private static LiftedItem Branch(uint offset, string condition, uint target) => new()
    {
        Offset = offset,
        Flow = new BranchFlow(
            new ScriptUnary(ScriptUnaryOperator.Not, new ScriptVariableReference(condition[3..])),
            target),
    };
}
