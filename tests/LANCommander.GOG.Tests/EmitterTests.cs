using System.Text.RegularExpressions;
using LANCommander.GOG.Script.Ast;
using LANCommander.GOG.Script.Emit;
using LANCommander.GOG.Script.Ifps;

namespace LANCommander.GOG.Tests;

/// <summary>
/// Covers what the emitters make of a recovered routine, and the places where PowerShell and
/// Pascal disagree about what a construct means.
/// </summary>
public class EmitterTests
{
    [Fact]
    public void PascalWritesAFunctionWithItsDeclarations()
    {
        var pascal = PascalEmitter.Emit(Sample());

        Assert.Contains("function Greet(Name: String; var Answer: BOOLEAN): BOOLEAN;", pascal);
        Assert.Contains("Local1: Integer;", pascal);
        Assert.Contains("Result := True;", pascal);
    }

    [Fact]
    public void PascalPutsNoSemicolonBeforeElse()
    {
        var pascal = PascalEmitter.Emit(Sample());

        // "end;" before "else" is the mistake that makes the output stop being Pascal.
        Assert.DoesNotContain("end;\nelse", pascal.Replace("\r\n", "\n"));
        Assert.Matches(new Regex(@"end\r?\n\s*else"), pascal);
    }

    [Fact]
    public void PowerShellDiscardsResultsItDoesNotUse()
    {
        var powershell = PowerShellEmitter.Emit(Sample());

        // An unsuppressed expression becomes the function's return value, so a call made only for
        // its effect has to be assigned away.
        Assert.Contains("$null = (InnoLog", powershell);
    }

    [Fact]
    public void PowerShellReturnsTheResultOnceAtTheEnd()
    {
        var powershell = PowerShellEmitter.Emit(Sample());

        Assert.Contains("$Result = $false", powershell);
        Assert.Contains("return $Result", powershell);
    }

    [Fact]
    public void PowerShellPassesReferenceParametersThrough()
    {
        var powershell = PowerShellEmitter.Emit(Sample());

        Assert.Contains("[ref] $Answer", powershell);

        // Reading a reference goes through its value; passing it on does not wrap it again.
        Assert.Contains("$Answer.Value", powershell);
    }

    [Fact]
    public void PowerShellDefersAmbiguousOperatorsToTheShim()
    {
        var logical = Emit(new ScriptAssignment(
            new ScriptVariableReference("A"),
            new ScriptBinary(
                ScriptBinaryOperator.And,
                new ScriptBinary(ScriptBinaryOperator.Equal, new ScriptVariableReference("B"), Number(1)),
                new ScriptBinary(ScriptBinaryOperator.Equal, new ScriptVariableReference("C"), Number(2)))));

        // Both operands are comparisons, so the logical reading is certain.
        Assert.Contains("-and", logical);
        Assert.DoesNotContain("InnoAnd", logical);

        var ambiguous = Emit(new ScriptAssignment(
            new ScriptVariableReference("A"),
            new ScriptBinary(
                ScriptBinaryOperator.And,
                new ScriptVariableReference("B"),
                new ScriptVariableReference("C"))));

        // One instruction covers both readings, so an undecidable case goes to the shim.
        Assert.Contains("InnoAnd", ambiguous);
    }

    [Fact]
    public void PowerShellRefusesToTranslateAJumpItCannotExpress()
    {
        var output = Emit(new ScriptGoto("L1234"));

        Assert.Contains("throw", output);
        Assert.Contains("L1234", output);
    }

    [Fact]
    public void PowerShellQuotesStringsWithoutInterpolatingThem()
    {
        var output = Emit(new ScriptAssignment(new ScriptVariableReference("A"), Text("{app}\\$x's")));

        Assert.Contains("'{app}\\$x''s'", output);
    }

    [Fact]
    public void PowerShellEscapesStringsThatWillNotFitInSingleQuotes()
    {
        var output = Emit(new ScriptAssignment(new ScriptVariableReference("A"), Text("a\r\nb")));

        Assert.Contains("\"a`r`nb\"", output);
    }

    [Fact]
    public void PowerShellStubsEveryInnoFunctionTheShimDoesNotProvide()
    {
        var output = PowerShellEmitter.Emit(Unit(
            new ScriptCallStatement(new ScriptCall("WIZARDFORM", [], ScriptCallKind.Host))));

        // A missing API fails where it is reached rather than resolving to nothing.
        Assert.Contains("function InnoWizardform { throw", output);
        Assert.Contains("(InnoWizardform)", output);
    }

    [Fact]
    public void EveryMappedShimFunctionExistsInTheModule()
    {
        var module = InnoApi.ModuleText();

        var names = new[]
        {
            "EXPANDCONSTANT", "FILEEXISTS", "REGWRITESTRINGVALUE", "GETINISTRING", "EXEC",
            "SETARRAYLENGTH", "FINDFIRST", "MSGBOX", "COPY", "POS", "DELETE", "LENGTH",
        };

        foreach (var name in names)
        {
            var shim = InnoApi.Resolve(name);

            Assert.NotNull(shim);
            Assert.Contains($"function {shim} ", module);
        }
    }

    [Fact]
    public void TheShimProvidesTheOperatorsTheEmitterDefersToIt()
    {
        var module = InnoApi.ModuleText();

        foreach (var helper in new[] { "InnoAnd", "InnoOr", "InnoXor", "InnoNot", "InnoIn", "InnoIs" })
            Assert.Contains($"function {helper} ", module);

        Assert.Contains("function Initialize-InnoEnvironment", module);
        Assert.Contains("function Invoke-InnoMethod", module);
        Assert.Contains("function Invoke-InnoNative", module);
    }

    private static string Emit(ScriptStatement statement) => PowerShellEmitter.Emit(Unit(statement));

    private static ScriptUnit Unit(ScriptStatement statement) =>
        new([], [new ScriptRoutine
        {
            Name = "Only",
            Parameters = [],
            Locals = [],
            Body = [statement],
            IsFullyStructured = true,
        }], null);

    private static ScriptExpression Number(long value) =>
        new ScriptLiteral(
            new IfpsValue { BaseType = IfpsBaseType.S32, Encoded = BitConverter.GetBytes((int)value) },
            IsBoolean: false);

    private static ScriptExpression Text(string value)
    {
        var bytes = new List<byte>(BitConverter.GetBytes(value.Length));

        bytes.AddRange(System.Text.Encoding.Latin1.GetBytes(value));

        return new ScriptLiteral(new IfpsValue { BaseType = IfpsBaseType.String, Encoded = bytes.ToArray() });
    }

    /// <summary>
    /// A routine holding one of each construct the emitters have to get right: a reference
    /// parameter, a discarded call, a result, and a conditional with both branches.
    /// </summary>
    private static ScriptUnit Sample()
    {
        var routine = new ScriptRoutine
        {
            Name = "Greet",
            ReturnType = "BOOLEAN",
            Parameters =
            [
                new ScriptVariable("Name", "String"),
                new ScriptVariable("Answer", "BOOLEAN", ByReference: true),
            ],
            Locals = [new ScriptVariable("Local1", "Integer")],
            Body =
            [
                new ScriptCallStatement(new ScriptCall(
                    "LOG",
                    [new ScriptArgument(new ScriptVariableReference("Name"), false)],
                    ScriptCallKind.Host)),
                new ScriptIf(
                    new ScriptVariableReference("Answer"),
                    [new ScriptAssignment(new ScriptVariableReference("Result"), Boolean(true))],
                    [new ScriptAssignment(new ScriptVariableReference("Result"), Boolean(false))]),
            ],
            IsFullyStructured = true,
        };

        return new ScriptUnit([], [routine], "Greet");
    }

    private static ScriptExpression Boolean(bool value) =>
        new ScriptLiteral(new IfpsValue { BaseType = IfpsBaseType.U8, Encoded = new[] { value ? (byte)1 : (byte)0 } });
}
