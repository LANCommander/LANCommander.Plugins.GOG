using LANCommander.GOG.Manifest;
using LANCommander.GOG.Packaging;
using LANCommander.GOG.Script.Ast;
using LANCommander.GOG.Script.Ifps;

namespace LANCommander.GOG.Tests;

/// <summary>
/// Covers which of an installer script's calls make it into the install script, and which do not.
/// </summary>
public class ScriptEffectTests
{
    private const long HkeyLocalMachine = unchecked((int)0x80000002);

    [Fact]
    public void ARegistryWriteReachedFromTheStepRoutineIsCarriedOver()
    {
        var unit = Unit(
            Routine("CURSTEPCHANGED", Call("WRITESETTINGS", ScriptCallKind.Script)),
            Routine("WRITESETTINGS", Call("REGWRITESTRINGVALUE", ScriptCallKind.Host,
                Number(HkeyLocalMachine), Text(@"SOFTWARE\Test"), Text("Path"), ExpandConstant(@"{app}\bin"))));

        var effects = ScriptEffectExtractor.Extract(unit);

        var write = Assert.IsType<ScriptRegistryWrite>(Assert.Single(effects.Effects));

        Assert.Equal(RegistryRoot.LocalMachine, write.Entry.Root);
        Assert.Equal(@"SOFTWARE\Test", write.Entry.Subkey);
        Assert.Equal(@"{app}\bin", write.Entry.ValueData);
    }

    [Fact]
    public void RoutinesTheStepRoutineNeverReachesAreIgnored()
    {
        var unit = Unit(
            Routine("CURSTEPCHANGED"),
            Routine("INITIALIZEWIZARD", Call("FORCEDIRECTORIES", ScriptCallKind.Host, ExpandConstant(@"{app}\saves"))));

        Assert.Empty(ScriptEffectExtractor.Extract(unit).Effects);
    }

    [Fact]
    public void AScriptWithoutAStepRoutineHasNoEffects()
    {
        var unit = Unit(Routine("INITIALIZESETUP", Call("FORCEDIRECTORIES", ScriptCallKind.Host, ExpandConstant(@"{app}\saves"))));

        Assert.Same(ScriptEffects.None, ScriptEffectExtractor.Extract(unit));
    }

    [Fact]
    public void AGlobalAssignedOneConstantIsResolvedWhereverItIsRead()
    {
        // GOG's script sets the support directory at one step and deletes it at another.
        var unit = new ScriptUnit(
            [new ScriptVariable("Global1", "String")],
            [
                Routine("CURSTEPCHANGED",
                    new ScriptAssignment(new ScriptVariableReference("Global1"), ExpandConstant(@"{app}\__support")),
                    Call("DELTREE", ScriptCallKind.Host,
                        new ScriptVariableReference("Global1"), Boolean(true), Boolean(true), Boolean(true))),
            ],
            null);

        var effect = Assert.Single(ScriptEffectExtractor.Extract(unit).Effects);

        Assert.Equal(new ScriptDeleteDirectory(@"{app}\__support", Recursive: true), effect);
    }

    [Fact]
    public void AnArgumentOnlyKnownAtRunTimeIsSkippedAndReported()
    {
        var unit = new ScriptUnit(
            [new ScriptVariable("Global1", "String")],
            [
                Routine("CURSTEPCHANGED",
                    new ScriptAssignment(new ScriptVariableReference("Global1"), ExpandConstant(@"{app}\a")),
                    new ScriptAssignment(new ScriptVariableReference("Global1"), ExpandConstant(@"{app}\b")),
                    Call("DELETEFILE", ScriptCallKind.Host, new ScriptVariableReference("Global1"))),
            ],
            null);

        var effects = ScriptEffectExtractor.Extract(unit);

        Assert.Empty(effects.Effects);
        Assert.Equal(new SkippedScriptEffect("CURSTEPCHANGED", "DELETEFILE", "its arguments are only known at run time"),
            Assert.Single(effects.Skipped));
    }

    [Theory]
    [InlineData(@"{win}\system.ini")]
    [InlineData(@"{app}")]
    [InlineData(@"{app}\..\other")]
    [InlineData(@"{tmp}\file")]
    public void ADeletionOutsideTheInstallDirectoryIsRefused(string path)
    {
        var unit = Unit(Routine("CURSTEPCHANGED", Call("DELETEFILE", ScriptCallKind.Host, ExpandConstant(path))));

        var effects = ScriptEffectExtractor.Extract(unit);

        Assert.Empty(effects.Effects);
        Assert.Single(effects.Skipped);
    }

    [Fact]
    public void RunningAProgramIsReportedRatherThanCarriedOver()
    {
        var unit = Unit(Routine("CURSTEPCHANGED",
            Call("EXEC", ScriptCallKind.Host, ExpandConstant(@"{app}\setup.exe"), Text(""), Text(""), Number(0), Number(0),
                new ScriptVariableReference("Local1"))));

        var effects = ScriptEffectExtractor.Extract(unit);

        Assert.Empty(effects.Effects);
        Assert.Equal("EXEC", Assert.Single(effects.Skipped).Function);
    }

    [Fact]
    public void FileWritesKeepTheirOrderAndPathFunctionsAreFolded()
    {
        var unit = Unit(Routine("CURSTEPCHANGED",
            Call("FORCEDIRECTORIES", ScriptCallKind.Host,
                new ScriptCall("ADDBACKSLASH", [new ScriptArgument(ExpandConstant(@"{userdocs}\My Games"), false)],
                    ScriptCallKind.Host)),
            Call("SAVESTRINGTOFILE", ScriptCallKind.Host,
                new ScriptBinary(ScriptBinaryOperator.Add, ExpandConstant("{app}"), Text(@"\EULA.txt")),
                Text("terms"),
                Boolean(false)),
            Call("SETINIBOOL", ScriptCallKind.Host, Text("Video"), Text("Windowed"), Boolean(true), ExpandConstant(@"{app}\game.ini"))));

        var effects = ScriptEffectExtractor.Extract(unit).Effects;

        Assert.Equal(
            [
                new ScriptCreateDirectory(@"{userdocs}\My Games\"),
                new ScriptWriteFile(@"{app}\EULA.txt", "terms", Append: false),
                new ScriptIniWrite(new IniEntry(@"{app}\game.ini", "Video", "Windowed", "1", EntryConditions.None)),
            ],
            effects);
    }

    private static ScriptUnit Unit(params ScriptRoutine[] routines) => new([], routines, null);

    private static ScriptRoutine Routine(string name, params ScriptStatement[] body) => new()
    {
        Name = name,
        Parameters = [],
        Locals = [new ScriptVariable("Local1", "Integer")],
        Body = body,
        IsFullyStructured = true,
    };

    private static ScriptCallStatement Call(string callee, ScriptCallKind kind, params ScriptExpression[] arguments) =>
        new(new ScriptCall(callee, arguments.Select(argument => new ScriptArgument(argument, false)).ToList(), kind));

    private static ScriptExpression ExpandConstant(string value) =>
        new ScriptCall("EXPANDCONSTANT", [new ScriptArgument(Text(value), false)], ScriptCallKind.Host);

    private static ScriptExpression Number(long value) =>
        new ScriptLiteral(
            new IfpsValue { BaseType = IfpsBaseType.S32, Encoded = BitConverter.GetBytes((int)value) },
            IsBoolean: false);

    private static ScriptExpression Boolean(bool value) =>
        new ScriptLiteral(new IfpsValue { BaseType = IfpsBaseType.U8, Encoded = new[] { value ? (byte)1 : (byte)0 } });

    private static ScriptExpression Text(string value)
    {
        var bytes = new List<byte>(BitConverter.GetBytes(value.Length));

        bytes.AddRange(System.Text.Encoding.Latin1.GetBytes(value));

        return new ScriptLiteral(new IfpsValue { BaseType = IfpsBaseType.String, Encoded = bytes.ToArray() });
    }
}
