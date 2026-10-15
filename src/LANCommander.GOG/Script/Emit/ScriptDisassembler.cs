using System.Globalization;
using System.Text;
using LANCommander.GOG.Script.Ifps;

namespace LANCommander.GOG.Script.Emit;

/// <summary>
/// Renders a compiled script as a text listing: its tables, then one labelled block of
/// instructions per bytecode function.
/// </summary>
/// <remarks>
/// This is the lowest-level view of a script and the one to reach for when a decompilation
/// looks wrong, because it adds no interpretation of its own — every line corresponds to bytes
/// that are actually in the blob.
/// </remarks>
public static class ScriptDisassembler
{
    /// <summary>Renders the whole script.</summary>
    public static string Disassemble(IfpsScript script)
    {
        var output = new StringBuilder();

        WriteSummary(output, script);
        WriteTypes(output, script);
        WriteGlobals(output, script);
        WriteImports(output, script);

        foreach (var function in script.ScriptFunctions)
            WriteFunction(output, script, function);

        return output.ToString();
    }

    private static void WriteSummary(StringBuilder output, IfpsScript script)
    {
        output.AppendLine("; compiled Pascal Script, disassembled without being executed")
            .AppendLine($"; container version {script.Version}")
            .AppendLine($"; {script.Types.Count} types, {script.Functions.Count} functions " +
                        $"({script.ScriptFunctions.Count()} in bytecode), {script.Globals.Count} globals")
            .AppendLine($"; entry point {(script.EntryPoint?.DisplayName ?? "none")}")
            .AppendLine($"; bytecode begins at 0x{script.CodeSegmentOffset:X} and runs {script.CodeSegment.Length} bytes")
            .AppendLine();
    }

    private static void WriteTypes(StringBuilder output, IfpsScript script)
    {
        output.AppendLine("types");

        foreach (var type in script.Types)
            output.AppendLine($"  [{type.Index,4}] {Printable(type.DisplayName)} = {Printable(DescribeType(type, script))}");

        output.AppendLine();
    }

    private static void WriteGlobals(StringBuilder output, IfpsScript script)
    {
        if (script.Globals.Count == 0)
            return;

        output.AppendLine("globals");

        foreach (var global in script.Globals)
            output.AppendLine($"  [{global.Index,4}] {Printable(global.DisplayName)} : {TypeName(script, global.TypeIndex)}");

        output.AppendLine();
    }

    private static void WriteImports(StringBuilder output, IfpsScript script)
    {
        var imports = script.ExternalFunctions.ToList();

        if (imports.Count == 0)
            return;

        output.AppendLine("imports");

        foreach (var import in imports)
        {
            var detail = import.Kind switch
            {
                IfpsImportKind.Class => $"class {import.ClassName}.{import.MethodName}",
                IfpsImportKind.Dll => $"native {Printable(import.Declaration!)}",
                IfpsImportKind.Internal => $"host function '{import.Name}'" +
                    (import.Declaration is { Length: > 0 } descriptor
                        ? $", descriptor {Printable(descriptor)}"
                        : string.Empty),
                _ => Printable(import.Declaration ?? import.Name),
            };

            output.AppendLine($"  [{import.Index,4}] {Printable(import.DisplayName)} — {detail}");
        }

        output.AppendLine();
    }

    private static void WriteFunction(StringBuilder output, IfpsScript script, IfpsScriptFunction function)
    {
        output.AppendLine($"function [{function.Index}] {Printable(function.DisplayName)}")
            .AppendLine($"  ; 0x{function.CodeOffset:X}, {function.CodeLength} bytes" +
                        (function.IsExported ? $", {Signature(script, function)}" : string.Empty));

        var labels = CollectLabels(function);

        foreach (var instruction in function.Instructions)
        {
            if (labels.Contains(instruction.Offset))
                output.AppendLine($"  L_{instruction.Offset:X8}:");

            output.AppendLine($"    {Render(script, instruction, labels)}");
        }

        if (function.DecodeError is { } error)
            output.AppendLine($"  ; DECODE FAILED: {error}");

        output.AppendLine();
    }

    /// <summary>
    /// Renders an exported function's signature in Pascal terms, falling back to the raw
    /// declaration when it cannot be parsed.
    /// </summary>
    private static string Signature(IfpsScript script, IfpsScriptFunction function)
    {
        if (function.Signature is not { } signature)
            return $"exported as \"{Printable(function.ExportDeclaration ?? string.Empty)}\"";

        var parameters = signature.Parameters.Select((parameter, index) =>
            $"{(parameter.ByReference ? "var " : string.Empty)}Arg{index + 1}: {TypeName(script, parameter.TypeIndex)}");

        var arguments = signature.Parameters.Count == 0 ? string.Empty : $"({string.Join("; ", parameters)})";

        return signature.ReturnTypeIndex is { } returnType
            ? $"function {function.DisplayName}{arguments}: {TypeName(script, returnType)}"
            : $"procedure {function.DisplayName}{arguments}";
    }

    /// <summary>
    /// Replaces the bytes that are not text with escapes.
    /// </summary>
    /// <remarks>
    /// Import declarations pack the calling convention and parameter kinds into raw characters,
    /// so printing them unfiltered would put control bytes — including NUL — into the listing and
    /// make it register as a binary file.
    /// </remarks>
    private static string Printable(string value)
    {
        if (!value.Any(character => char.IsControl(character) || character > '\x7E'))
            return value;

        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
            if (char.IsControl(character) || character > '\x7E')
                builder.Append($"\\x{(int)character:X2}");
            else
                builder.Append(character);

        return builder.ToString();
    }

    /// <summary>Collects every offset inside the function that something branches to.</summary>
    private static HashSet<uint> CollectLabels(IfpsScriptFunction function)
    {
        var labels = new HashSet<uint>();

        foreach (var instruction in function.Instructions)
        {
            if (instruction.Target is { } target)
                labels.Add(target);

            if (instruction.ExceptionRegion is not { } region)
                continue;

            foreach (var boundary in new[] { region.Finally, region.Except, region.SecondFinally, region.End })
                if (boundary is { } offset)
                    labels.Add(offset);
        }

        return labels;
    }

    private static string Render(IfpsScript script, IfpsInstruction instruction, HashSet<uint> labels)
    {
        var mnemonic = Mnemonic(instruction);
        var operands = new List<string>();

        if (instruction.FunctionIndex is { } functionIndex)
            operands.Add(FunctionName(script, functionIndex));

        if (instruction.TypeIndex is { } typeIndex)
            operands.Add(TypeName(script, typeIndex));

        if (instruction.Target is { } target)
            operands.Add(Label(target, labels));

        if (instruction.ExceptionRegion is { } region)
        {
            operands.Add($"finally={Label(region.Finally, labels)}");
            operands.Add($"except={Label(region.Except, labels)}");
            operands.Add($"finally2={Label(region.SecondFinally, labels)}");
            operands.Add($"end={Label(region.End, labels)}");
        }

        if (instruction.HandlerPosition is { } position)
            operands.Add(position switch
            {
                0 => "end of block",
                1 => "end of finally",
                2 => "end of except",
                3 => "end of second finally",
                _ => position.ToString(CultureInfo.InvariantCulture),
            });

        operands.AddRange(instruction.Operands.Select(operand => RenderOperand(script, operand)));

        return operands.Count == 0
            ? mnemonic
            : $"{mnemonic,-12} {string.Join(", ", operands)}";
    }

    private static string Mnemonic(IfpsInstruction instruction) => instruction.OpCode switch
    {
        IfpsOpCode.Calculate => $"CALC.{instruction.AluOperator}".ToUpperInvariant(),
        IfpsOpCode.Compare => $"CMP.{instruction.CompareOperator}".ToUpperInvariant(),
        IfpsOpCode.SetFlag => instruction.NegateFlag == true ? "SETFLAG.NOT" : "SETFLAG",
        _ => instruction.OpCode.ToString().ToUpperInvariant(),
    };

    private static string Label(uint? offset, HashSet<uint> labels) =>
        offset is not { } value ? "-"
        : labels.Contains(value) ? $"L_{value:X8}"
        : $"0x{value:X}";

    private static string RenderOperand(IfpsScript script, IfpsOperand operand) => operand switch
    {
        IfpsVariableOperand variable => RenderAddress(script, variable.Address),
        IfpsImmediateOperand immediate => immediate.Value.ToLiteral(),
        IfpsIndexedOperand indexed =>
            $"{RenderAddress(script, indexed.Address)}[{indexed.Index.ToString(CultureInfo.InvariantCulture)}]",
        IfpsVariableIndexedOperand variableIndexed =>
            $"{RenderAddress(script, variableIndexed.Address)}[{RenderAddress(script, variableIndexed.IndexAddress)}]",
        _ => operand.ToString() ?? "?",
    };

    private static string RenderAddress(IfpsScript script, IfpsAddress address) =>
        address.IsGlobal && address.GlobalIndex < script.Globals.Count
            ? script.Globals[address.GlobalIndex].DisplayName
            : address.ToString();

    private static string FunctionName(IfpsScript script, uint index) =>
        index < (uint)script.Functions.Count
            ? Printable(script.Functions[(int)index].DisplayName)
            : $"<function {index.ToString(CultureInfo.InvariantCulture)}>";

    private static string TypeName(IfpsScript script, uint index) =>
        index < (uint)script.Types.Count
            ? Printable(script.Types[(int)index].DisplayName)
            : $"<type {index.ToString(CultureInfo.InvariantCulture)}>";

    private static string DescribeType(IfpsType type, IfpsScript script) => type switch
    {
        IfpsPrimitiveType primitive => primitive.PascalName,
        IfpsClassType classType => $"class {classType.ClassName}",
        IfpsInterfaceType interfaceType => $"interface {{{Convert.ToHexString(interfaceType.Guid.Span)}}}",
        IfpsSetType set => $"set of {set.BitSize} bits",
        IfpsArrayType array => $"array of {TypeName(script, array.ElementTypeIndex)}",
        IfpsStaticArrayType staticArray =>
            $"array[{staticArray.StartOffset}..{staticArray.StartOffset + staticArray.Size - 1}] of " +
            TypeName(script, staticArray.ElementTypeIndex),
        IfpsRecordType record =>
            $"record({string.Join(", ", record.FieldTypeIndices.Select(field => TypeName(script, field)))})",
        IfpsProcedurePointerType procedure =>
            $"{(procedure.ReturnsValue ? "function" : "procedure")} pointer, {procedure.ParameterCount} parameters",
        _ => type.BaseType.ToString(),
    };
}
