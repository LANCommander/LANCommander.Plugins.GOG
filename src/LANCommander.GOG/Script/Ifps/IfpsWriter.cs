using System.Buffers.Binary;
using System.Text;

namespace LANCommander.GOG.Script.Ifps;

/// <summary>
/// Serialises an <see cref="IfpsScript"/> back to the bytes it was read from.
/// </summary>
/// <remarks>
/// <para>
/// This exists to prove the reader is right. GOG's installers ship no script source, so there is
/// nothing to compare a decompilation against; what can be checked is that reading and rewriting
/// a script reproduces it byte for byte. A field read with the wrong width, or skipped entirely,
/// shows up immediately as a difference.
/// </para>
/// <para>
/// The bytecode area is copied verbatim rather than re-encoded from the decoded instructions.
/// Function bodies address that area absolutely, so rewriting it would invalidate every offset
/// in the function table. Instruction encoding is checked separately, per function, by
/// <see cref="VerifyInstructionEncoding"/>.
/// </para>
/// </remarks>
public static class IfpsWriter
{
    /// <summary>Writes a script to a new byte array.</summary>
    public static byte[] Write(IfpsScript script)
    {
        var buffer = new MemoryStream();
        var header = new byte[IfpsScript.HeaderSize];

        script.WriteHeader(header);
        buffer.Write(header);

        foreach (var type in script.Types)
            WriteType(buffer, type, script.StoresTypeAttributes);

        foreach (var function in script.Functions)
            WriteFunction(buffer, function);

        foreach (var global in script.Globals)
            WriteGlobal(buffer, global);

        buffer.Write(script.CodeSegment.Span);

        return buffer.ToArray();
    }

    /// <summary>
    /// Re-encodes every decoded instruction and reports the ones whose bytes differ from the
    /// original, which is how the instruction decoder is held to the same standard as the
    /// container reader.
    /// </summary>
    /// <param name="script">A loaded script.</param>
    /// <param name="blob">The bytes the script was loaded from.</param>
    /// <returns>One message per mismatch, empty when every instruction round-trips.</returns>
    public static IReadOnlyList<string> VerifyInstructionEncoding(IfpsScript script, ReadOnlyMemory<byte> blob)
    {
        var problems = new List<string>();

        foreach (var function in script.ScriptFunctions)
        {
            if (function.DecodeError is { } error)
            {
                problems.Add($"{function.DisplayName}: {error}");

                continue;
            }

            var consumed = function.Instructions.Sum(instruction => instruction.Length);

            if (consumed != function.CodeLength)
            {
                problems.Add(
                    $"{function.DisplayName}: decoded {consumed} bytes but the function declares {function.CodeLength}.");

                continue;
            }

            foreach (var instruction in function.Instructions)
            {
                var original = blob.Span.Slice((int)instruction.Offset, instruction.Length);
                var reencoded = EncodeInstruction(instruction);

                if (!original.SequenceEqual(reencoded))
                    problems.Add(
                        $"{function.DisplayName}: {instruction.OpCode} at 0x{instruction.Offset:X} re-encodes as " +
                        $"{Convert.ToHexString(reencoded)} but reads {Convert.ToHexString(original)}.");
            }
        }

        return problems;
    }

    private static void WriteType(MemoryStream buffer, IfpsType type, bool storesAttributes)
    {
        var raw = (byte)((byte)type.BaseType | (type.ExportName is null ? 0 : 0x80));

        buffer.WriteByte(raw);

        switch (type)
        {
            case IfpsClassType classType:
                WriteLengthPrefixed(buffer, Encoding.Latin1.GetBytes(classType.ClassName));

                break;

            case IfpsProcedurePointerType procedurePointer:
                WriteLengthPrefixed(buffer, procedurePointer.ParameterInfo.Span);

                break;

            case IfpsInterfaceType interfaceType:
                buffer.Write(interfaceType.Guid.Span);

                break;

            case IfpsSetType set:
                WriteUInt32(buffer, set.BitSize);

                break;

            case IfpsStaticArrayType staticArray:
                WriteUInt32(buffer, staticArray.ElementTypeIndex);
                WriteUInt32(buffer, staticArray.Size);
                WriteUInt32(buffer, staticArray.StartOffset);

                break;

            case IfpsArrayType array:
                WriteUInt32(buffer, array.ElementTypeIndex);

                break;

            case IfpsRecordType record:
                WriteUInt32(buffer, (uint)record.FieldTypeIndices.Count);

                foreach (var field in record.FieldTypeIndices)
                    WriteUInt32(buffer, field);

                break;
        }

        if (type.ExportName is { } exportName)
            WriteLengthPrefixed(buffer, Encoding.Latin1.GetBytes(exportName));

        if (storesAttributes)
            WriteAttributes(buffer, type.Attributes);
    }

    private static void WriteFunction(MemoryStream buffer, IfpsFunction function)
    {
        buffer.WriteByte(function.Flags);

        switch (function)
        {
            case IfpsExternalFunction external:
            {
                var name = Encoding.Latin1.GetBytes(external.Name);

                buffer.WriteByte(checked((byte)name.Length));
                buffer.Write(name);

                if ((function.Flags & 3) == 3)
                    WriteLengthPrefixed(buffer, Encoding.Latin1.GetBytes(external.Declaration ?? string.Empty));

                break;
            }

            case IfpsScriptFunction script:
            {
                WriteUInt32(buffer, script.CodeOffset);
                WriteUInt32(buffer, script.CodeLength);

                if ((function.Flags & 2) != 0)
                {
                    WriteLengthPrefixed(buffer, Encoding.Latin1.GetBytes(script.ExportName ?? string.Empty));
                    WriteLengthPrefixed(buffer, Encoding.Latin1.GetBytes(script.ExportDeclaration ?? string.Empty));
                }

                break;
            }
        }

        if ((function.Flags & 4) != 0)
            WriteAttributes(buffer, function.Attributes);
    }

    private static void WriteGlobal(MemoryStream buffer, IfpsGlobal global)
    {
        WriteUInt32(buffer, global.TypeIndex);
        buffer.WriteByte(global.Flags);

        if (global.ExportName is { } exportName)
            WriteLengthPrefixed(buffer, Encoding.Latin1.GetBytes(exportName));
    }

    private static void WriteAttributes(MemoryStream buffer, IReadOnlyList<IfpsAttribute> attributes)
    {
        WriteUInt32(buffer, (uint)attributes.Count);

        foreach (var attribute in attributes)
        {
            WriteLengthPrefixed(buffer, Encoding.Latin1.GetBytes(attribute.Name));
            WriteUInt32(buffer, (uint)attribute.Fields.Count);

            foreach (var field in attribute.Fields)
            {
                WriteUInt32(buffer, field.TypeIndex);
                buffer.Write(field.Value.Encoded.Span);
            }
        }
    }

    private static byte[] EncodeInstruction(IfpsInstruction instruction)
    {
        var buffer = new MemoryStream();

        buffer.WriteByte((byte)instruction.OpCode);

        switch (instruction.OpCode)
        {
            case IfpsOpCode.Calculate:
                buffer.WriteByte((byte)instruction.AluOperator!.Value);

                break;

            case IfpsOpCode.Compare:
                buffer.WriteByte((byte)instruction.CompareOperator!.Value);

                break;

            case IfpsOpCode.Call:
                WriteUInt32(buffer, instruction.FunctionIndex!.Value);

                break;

            case IfpsOpCode.PushType:
                WriteUInt32(buffer, instruction.TypeIndex!.Value);

                break;

            case IfpsOpCode.Goto or IfpsOpCode.FlagGoto or IfpsOpCode.PopAndGoto or IfpsOpCode.PopTwoAndGoto:
                WriteUInt32(buffer, unchecked(instruction.Target!.Value - instruction.NextOffset));

                break;

            case IfpsOpCode.ConditionalGoto or IfpsOpCode.ConditionalNotGoto:
                WriteUInt32(buffer, unchecked(instruction.Target!.Value - instruction.NextOffset));

                break;

            case IfpsOpCode.PushExceptionHandler:
            {
                var region = instruction.ExceptionRegion!.Value;
                var origin = instruction.NextOffset;

                WriteUInt32(buffer, Relative(region.Finally, origin));
                WriteUInt32(buffer, Relative(region.Except, origin));
                WriteUInt32(buffer, Relative(region.SecondFinally, origin));
                WriteUInt32(buffer, Relative(region.End, origin));

                break;
            }

            case IfpsOpCode.PopExceptionHandler:
                buffer.WriteByte(instruction.HandlerPosition!.Value);

                break;
        }

        foreach (var operand in instruction.Operands)
            WriteOperand(buffer, operand);

        if (instruction.OpCode == IfpsOpCode.SetFlag)
            buffer.WriteByte(instruction.NegateFlag!.Value ? (byte)1 : (byte)0);

        return buffer.ToArray();
    }

    private static void WriteOperand(MemoryStream buffer, IfpsOperand operand)
    {
        switch (operand)
        {
            case IfpsVariableOperand variable:
                buffer.WriteByte(0);
                WriteUInt32(buffer, variable.Address.Raw);

                break;

            case IfpsImmediateOperand immediate:
                buffer.WriteByte(1);
                WriteUInt32(buffer, immediate.TypeIndex);
                buffer.Write(immediate.Value.Encoded.Span);

                break;

            case IfpsIndexedOperand indexed:
                buffer.WriteByte(2);
                WriteUInt32(buffer, indexed.Address.Raw);
                WriteUInt32(buffer, indexed.Index);

                break;

            case IfpsVariableIndexedOperand variableIndexed:
                buffer.WriteByte(3);
                WriteUInt32(buffer, variableIndexed.Address.Raw);
                WriteUInt32(buffer, variableIndexed.IndexAddress.Raw);

                break;
        }
    }

    private static uint Relative(uint? target, uint origin) =>
        target is { } value ? unchecked(value - origin) : 0xFFFFFFFF;

    private static void WriteUInt32(MemoryStream buffer, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        buffer.Write(bytes);
    }

    private static void WriteLengthPrefixed(MemoryStream buffer, ReadOnlySpan<byte> bytes)
    {
        WriteUInt32(buffer, (uint)bytes.Length);
        buffer.Write(bytes);
    }
}
