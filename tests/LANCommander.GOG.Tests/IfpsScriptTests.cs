using System.Text;
using LANCommander.GOG.Script.Ifps;

namespace LANCommander.GOG.Tests;

/// <summary>
/// Covers the compiled-script reader against blobs built by hand, so a failure points at one
/// record rather than at "a real installer stopped parsing".
/// </summary>
public class IfpsScriptTests
{
    [Fact]
    public void RejectsABlobWithoutTheMagic()
    {
        var exception = Assert.Throws<IfpsException>(() => IfpsScript.Load(new byte[] { 1, 2, 3, 4 }));

        Assert.Contains("IFPS", exception.Message);
    }

    [Fact]
    public void RejectsAContainerVersionItDoesNotUnderstand()
    {
        var blob = new IfpsBlobBuilder(version: IfpsScript.HighestVersion + 1).Build();

        var exception = Assert.Throws<IfpsException>(() => IfpsScript.Load(blob));

        Assert.Contains("outside the supported range", exception.Message);
    }

    [Fact]
    public void ReadsTheTables()
    {
        var script = IfpsScript.Load(Sample());

        Assert.Equal(23u, script.Version);
        Assert.Equal(4, script.Types.Count);
        Assert.Equal(2, script.Functions.Count);
        Assert.Single(script.Globals);

        Assert.Equal("TMyString", script.Types[2].ExportName);
        Assert.Equal(IfpsBaseType.String, script.Types[2].BaseType);

        var record = Assert.IsType<IfpsRecordType>(script.Types[3]);

        Assert.Equal([0u, 1u], record.FieldTypeIndices);

        var imported = Assert.IsType<IfpsExternalFunction>(script.Functions[0]);

        Assert.Equal("MSGBOX", imported.Name);
        Assert.Equal(IfpsImportKind.Internal, imported.Kind);

        var main = Assert.IsType<IfpsScriptFunction>(script.Functions[1]);

        Assert.Equal("MAIN", main.ExportName);
        Assert.True(main.IsExported);
        Assert.Same(main, script.EntryPoint);

        Assert.Equal("GCOUNT", script.Globals[0].ExportName);
        Assert.Equal(1u, script.Globals[0].TypeIndex);
    }

    [Fact]
    public void DecodesInstructionsWithTheirOperands()
    {
        var script = IfpsScript.Load(Sample());
        var main = script.ScriptFunctions.Single();

        Assert.Null(main.DecodeError);
        Assert.Equal(3, main.Instructions.Count);

        var assign = main.Instructions[0];

        Assert.Equal(IfpsOpCode.Assign, assign.OpCode);

        var destination = Assert.IsType<IfpsVariableOperand>(assign.Operands[0]);

        Assert.False(destination.Address.IsGlobal);
        Assert.Equal(0, destination.Address.StackOffset);

        var source = Assert.IsType<IfpsImmediateOperand>(assign.Operands[1]);

        Assert.Equal(42, source.Value.AsInt64());
        Assert.Equal("42", source.Value.ToLiteral());

        Assert.Equal(IfpsOpCode.Call, main.Instructions[1].OpCode);
        Assert.Equal(0u, main.Instructions[1].FunctionIndex);

        Assert.Equal(IfpsOpCode.Return, main.Instructions[2].OpCode);
    }

    [Fact]
    public void ResolvesBranchTargetsToAbsoluteOffsets()
    {
        var builder = new IfpsBlobBuilder();

        builder.AddPrimitiveType(IfpsBaseType.U8);
        builder.AddScriptFunction("JUMPER", "-1", code =>
        {
            // GOTO forward over a RET, landing on the second RET.
            code.Goto(relative: 1);
            code.Return();
            code.Return();
        });

        var script = IfpsScript.Load(builder.Build());
        var function = script.ScriptFunctions.Single();

        Assert.Null(function.DecodeError);

        var jump = function.Instructions[0];

        Assert.Equal(IfpsOpCode.Goto, jump.OpCode);
        Assert.Equal(function.Instructions[2].Offset, jump.Target);
    }

    [Fact]
    public void RecordsWhyAFunctionCouldNotBeDecodedWithoutFailingTheWholeScript()
    {
        var builder = new IfpsBlobBuilder();

        builder.AddPrimitiveType(IfpsBaseType.U8);
        builder.AddScriptFunction("GOOD", "-1", code => code.Return());
        builder.AddScriptFunction("BAD", "-1", code => code.Raw(0xAB));

        var script = IfpsScript.Load(builder.Build());

        Assert.False(script.FullyDecoded);

        var good = script.ScriptFunctions.First(function => function.ExportName == "GOOD");
        var bad = script.ScriptFunctions.First(function => function.ExportName == "BAD");

        Assert.Null(good.DecodeError);
        Assert.Contains("Unknown opcode", bad.DecodeError);
    }

    [Fact]
    public void WritingAScriptReproducesTheBytesItWasReadFrom()
    {
        var blob = Sample();

        var rewritten = IfpsWriter.Write(IfpsScript.Load(blob));

        Assert.Equal(blob, rewritten);
    }

    [Fact]
    public void EveryInstructionReEncodesToTheBytesItWasReadFrom()
    {
        var blob = Sample();

        Assert.Empty(IfpsWriter.VerifyInstructionEncoding(IfpsScript.Load(blob), blob));
    }

    [Theory]
    [InlineData(IfpsBaseType.U8, new byte[] { 1 }, "True")]
    [InlineData(IfpsBaseType.U8, new byte[] { 7 }, "7")]
    [InlineData(IfpsBaseType.S32, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, "-1")]
    [InlineData(IfpsBaseType.U32, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, "4294967295")]
    public void RendersConstantsTheWayPascalWouldSpellThem(IfpsBaseType baseType, byte[] encoded, string expected)
    {
        var value = new IfpsValue { BaseType = baseType, Encoded = encoded };

        Assert.Equal(expected, value.ToLiteral());
    }

    [Fact]
    public void QuotesStringsAndEscapesWhatCannotBeTyped()
    {
        var encoded = new List<byte> { 5, 0, 0, 0 };

        encoded.AddRange("a'b\r\n"u8.ToArray());

        var value = new IfpsValue { BaseType = IfpsBaseType.String, Encoded = encoded.ToArray() };

        Assert.Equal("a'b", value.AsString()[..3]);
        Assert.Equal("'a''b'#13#10", value.ToLiteral());
    }

    [Fact]
    public void ReadsAnExportedSignature()
    {
        // "@" passes a parameter in; "!" passes it by reference.
        var signature = IfpsSignature.Parse("27 !17 @5");

        Assert.NotNull(signature);
        Assert.Equal(27u, signature.ReturnTypeIndex);
        Assert.Equal(2, signature.Parameters.Count);
        Assert.True(signature.Parameters[0].ByReference);
        Assert.Equal(17u, signature.Parameters[0].TypeIndex);
        Assert.False(signature.Parameters[1].ByReference);
    }

    [Fact]
    public void ReadsAProcedureSignature()
    {
        var signature = IfpsSignature.Parse("-1 @5");

        Assert.NotNull(signature);
        Assert.Null(signature.ReturnTypeIndex);
        Assert.False(signature.ReturnsValue);
        Assert.Single(signature.Parameters);
    }

    [Fact]
    public void SeparatesGlobalAddressesFromStackAddresses()
    {
        Assert.True(new IfpsAddress(12).IsGlobal);
        Assert.Equal(12, new IfpsAddress(12).GlobalIndex);

        // Locals sit at and above the stack base; parameters sit below it.
        Assert.False(new IfpsAddress(IfpsAddress.StackStart + 3).IsGlobal);
        Assert.Equal(3, new IfpsAddress(IfpsAddress.StackStart + 3).StackOffset);
        Assert.Equal(-2, new IfpsAddress(IfpsAddress.StackStart - 2).StackOffset);
    }

    /// <summary>
    /// A container holding one of each interesting record: a plain type, an exported type, a
    /// record type, an imported function, an exported bytecode function, and a global.
    /// </summary>
    private static byte[] Sample()
    {
        var builder = new IfpsBlobBuilder();

        builder.AddPrimitiveType(IfpsBaseType.U8);
        builder.AddPrimitiveType(IfpsBaseType.S32);
        builder.AddPrimitiveType(IfpsBaseType.String, exportName: "TMyString");
        builder.AddRecordType([0, 1]);

        builder.AddImportedFunction("MSGBOX");
        builder.AddScriptFunction("MAIN", "-1", code =>
        {
            code.Assign(IfpsAddress.StackStart, typeIndex: 1, value: BitConverter.GetBytes(42));
            code.Call(0);
            code.Return();
        });

        builder.AddGlobal(typeIndex: 1, exportName: "GCOUNT");
        builder.EntryPoint = 1;

        return builder.Build();
    }
}

/// <summary>
/// Builds compiled-script blobs a byte at a time, so tests can state exactly what a reader is
/// being handed.
/// </summary>
internal sealed class IfpsBlobBuilder(uint version = 23)
{
    private readonly MemoryStream _types = new();
    private readonly MemoryStream _functions = new();
    private readonly MemoryStream _globals = new();
    private readonly List<(long HeaderPosition, byte[] Code)> _bodies = [];

    private int _typeCount;
    private int _functionCount;
    private int _globalCount;

    /// <summary>Index of the entry point, or <c>null</c> when the script has none.</summary>
    public uint? EntryPoint { get; set; }

    public void AddPrimitiveType(IfpsBaseType baseType, string? exportName = null)
    {
        _types.WriteByte((byte)((byte)baseType | (exportName is null ? 0 : 0x80)));

        FinishType(exportName);
    }

    public void AddRecordType(uint[] fieldTypes, string? exportName = null)
    {
        _types.WriteByte((byte)((byte)IfpsBaseType.Record | (exportName is null ? 0 : 0x80)));

        WriteUInt32(_types, (uint)fieldTypes.Length);

        foreach (var field in fieldTypes)
            WriteUInt32(_types, field);

        FinishType(exportName);
    }

    private void FinishType(string? exportName)
    {
        if (exportName is not null)
            WriteLongString(_types, exportName);

        // Containers at version 21 and above store an attribute count on every type.
        if (version >= 21)
            WriteUInt32(_types, 0);

        _typeCount++;
    }

    public void AddImportedFunction(string name)
    {
        _functions.WriteByte(1);
        _functions.WriteByte((byte)name.Length);
        _functions.Write(Encoding.Latin1.GetBytes(name));

        _functionCount++;
    }

    public void AddScriptFunction(string exportName, string declaration, Action<CodeBuilder> body)
    {
        var code = new CodeBuilder();

        body(code);

        _functions.WriteByte(2);

        // The real offset is only known once the tables are sized, so it is patched in Build.
        _bodies.Add((_functions.Position, code.ToArray()));

        WriteUInt32(_functions, 0);
        WriteUInt32(_functions, (uint)code.Length);
        WriteLongString(_functions, exportName);
        WriteLongString(_functions, declaration);

        _functionCount++;
    }

    public void AddGlobal(uint typeIndex, string? exportName = null)
    {
        WriteUInt32(_globals, typeIndex);
        _globals.WriteByte((byte)(exportName is null ? 0 : 1));

        if (exportName is not null)
            WriteLongString(_globals, exportName);

        _globalCount++;
    }

    public byte[] Build()
    {
        var functions = _functions.ToArray();
        var codeStart = 28 + _types.Length + functions.Length + _globals.Length;
        var offset = codeStart;

        foreach (var (headerPosition, code) in _bodies)
        {
            BitConverter.GetBytes((uint)offset).CopyTo(functions, (int)headerPosition);
            offset += code.Length;
        }

        var blob = new MemoryStream();

        blob.Write("IFPS"u8);
        WriteUInt32(blob, version);
        WriteUInt32(blob, (uint)_typeCount);
        WriteUInt32(blob, (uint)_functionCount);
        WriteUInt32(blob, (uint)_globalCount);
        WriteUInt32(blob, EntryPoint ?? 0xFFFFFFFF);
        WriteUInt32(blob, 0);

        blob.Write(_types.ToArray());
        blob.Write(functions);
        blob.Write(_globals.ToArray());

        foreach (var (_, code) in _bodies)
            blob.Write(code);

        return blob.ToArray();
    }

    private static void WriteUInt32(Stream stream, uint value) => stream.Write(BitConverter.GetBytes(value));

    private static void WriteLongString(Stream stream, string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);

        WriteUInt32(stream, (uint)bytes.Length);
        stream.Write(bytes);
    }

    /// <summary>Emits bytecode for one function.</summary>
    internal sealed class CodeBuilder
    {
        private readonly MemoryStream _code = new();

        public int Length => (int)_code.Length;

        public void Assign(uint destination, uint typeIndex, byte[] value)
        {
            _code.WriteByte((byte)IfpsOpCode.Assign);
            WriteVariable(destination);
            WriteImmediate(typeIndex, value);
        }

        public void Call(uint functionIndex)
        {
            _code.WriteByte((byte)IfpsOpCode.Call);
            WriteUInt32(_code, functionIndex);
        }

        /// <param name="relative">Bytes to skip, measured from the end of this instruction.</param>
        public void Goto(int relative)
        {
            _code.WriteByte((byte)IfpsOpCode.Goto);
            WriteUInt32(_code, unchecked((uint)relative));
        }

        public void Return() => _code.WriteByte((byte)IfpsOpCode.Return);

        public void Raw(byte value) => _code.WriteByte(value);

        public byte[] ToArray() => _code.ToArray();

        private void WriteVariable(uint address)
        {
            _code.WriteByte(0);
            WriteUInt32(_code, address);
        }

        private void WriteImmediate(uint typeIndex, byte[] value)
        {
            _code.WriteByte(1);
            WriteUInt32(_code, typeIndex);
            _code.Write(value);
        }
    }
}
