namespace LANCommander.GOG.Script.Ifps;

/// <summary>
/// Turns a run of bytecode into instructions.
/// </summary>
/// <remarks>
/// Every branch offset in the format is stored relative to the end of the instruction that
/// carries it. They are resolved to absolute offsets here, once, so that nothing downstream has
/// to know how long an instruction was.
/// </remarks>
internal static class InstructionDecoder
{
    /// <summary>The value a branch target takes when there is no target.</summary>
    private const uint NoTarget = 0xFFFFFFFF;

    /// <summary>
    /// Decodes the body of a function.
    /// </summary>
    /// <param name="reader">A reader over the whole compiled blob.</param>
    /// <param name="offset">Absolute offset of the function's first instruction.</param>
    /// <param name="length">Length of the function's bytecode, in bytes.</param>
    /// <param name="types">The script's type table, used to size inline constants.</param>
    /// <param name="instructions">Filled as decoding proceeds, so a failure leaves what was read.</param>
    /// <exception cref="IfpsException">The bytecode is malformed or overruns the function.</exception>
    public static void Decode(
        IfpsReader reader,
        uint offset,
        uint length,
        IReadOnlyList<IfpsType> types,
        List<IfpsInstruction> instructions)
    {
        var start = checked((int)offset);
        var end = checked((int)(offset + length));

        if (end > reader.Length)
            throw new IfpsException(
                $"Function body at 0x{offset:X} runs {length} bytes, past the end of the {reader.Length}-byte script.");

        var cursor = reader.At(start);

        while (cursor.Position < end)
        {
            var instruction = DecodeOne(cursor, types);

            if (instruction.NextOffset > end)
                throw new IfpsException(
                    $"Instruction {instruction.OpCode} at 0x{instruction.Offset:X} overruns the end of its function at 0x{end:X}.");

            instructions.Add(instruction);
        }
    }

    private static IfpsInstruction DecodeOne(IfpsReader reader, IReadOnlyList<IfpsType> types)
    {
        var offset = (uint)reader.Position;
        var opCode = (IfpsOpCode)reader.ReadByte();

        IfpsInstruction Build(
            IReadOnlyList<IfpsOperand>? operands = null,
            uint? target = null,
            uint? functionIndex = null,
            uint? typeIndex = null,
            IfpsAluOperator? alu = null,
            IfpsCompareOperator? compare = null,
            bool? negateFlag = null,
            byte? handlerPosition = null,
            IfpsExceptionRegion? region = null) =>
            new()
            {
                Offset = offset,
                Length = reader.Position - (int)offset,
                OpCode = opCode,
                Operands = operands ?? [],
                Target = target,
                FunctionIndex = functionIndex,
                TypeIndex = typeIndex,
                AluOperator = alu,
                CompareOperator = compare,
                NegateFlag = negateFlag,
                HandlerPosition = handlerPosition,
                ExceptionRegion = region,
            };

        switch (opCode)
        {
            case IfpsOpCode.Assign or IfpsOpCode.SetPointer or IfpsOpCode.SetStackPointerToCopy:
            {
                var destination = ReadOperand(reader, types);
                var source = ReadOperand(reader, types);

                return Build([destination, source]);
            }

            case IfpsOpCode.Calculate:
            {
                var alu = (IfpsAluOperator)reader.ReadByte();
                var destination = ReadOperand(reader, types);
                var source = ReadOperand(reader, types);

                return Build([destination, source], alu: alu);
            }

            case IfpsOpCode.Push or IfpsOpCode.PushVar or IfpsOpCode.CallVar or IfpsOpCode.BooleanNot
                or IfpsOpCode.Negate or IfpsOpCode.IntegerNot
                or IfpsOpCode.Increment or IfpsOpCode.Decrement:
                return Build([ReadOperand(reader, types)]);

            case IfpsOpCode.Pop or IfpsOpCode.Return or IfpsOpCode.Nop:
                return Build();

            case IfpsOpCode.Call:
                return Build(functionIndex: reader.ReadUInt32());

            case IfpsOpCode.Goto or IfpsOpCode.FlagGoto or IfpsOpCode.PopAndGoto or IfpsOpCode.PopTwoAndGoto:
            {
                var relative = reader.ReadUInt32();

                return Build(target: unchecked((uint)reader.Position + relative));
            }

            case IfpsOpCode.ConditionalGoto or IfpsOpCode.ConditionalNotGoto:
            {
                var relative = reader.ReadUInt32();
                var condition = ReadOperand(reader, types);

                return Build([condition], target: unchecked((uint)reader.Position + relative));
            }

            case IfpsOpCode.PushType:
                return Build(typeIndex: reader.ReadUInt32());

            case IfpsOpCode.Compare:
            {
                var compare = (IfpsCompareOperator)reader.ReadByte();
                var destination = ReadOperand(reader, types);
                var left = ReadOperand(reader, types);
                var right = ReadOperand(reader, types);

                return Build([destination, left, right], compare: compare);
            }

            case IfpsOpCode.SetFlag:
            {
                var operand = ReadOperand(reader, types);
                var negate = reader.ReadBoolean();

                return Build([operand], negateFlag: negate);
            }

            case IfpsOpCode.PushExceptionHandler:
            {
                var finallyOffset = reader.ReadUInt32();
                var exceptOffset = reader.ReadUInt32();
                var secondFinallyOffset = reader.ReadUInt32();
                var endOffset = reader.ReadUInt32();

                var origin = (uint)reader.Position;

                uint? Resolve(uint relative) => relative == NoTarget ? null : unchecked(origin + relative);

                return Build(region: new IfpsExceptionRegion(
                    Resolve(finallyOffset),
                    Resolve(exceptOffset),
                    Resolve(secondFinallyOffset),
                    Resolve(endOffset)));
            }

            case IfpsOpCode.PopExceptionHandler:
                return Build(handlerPosition: reader.ReadByte());

            default:
                throw new IfpsException(
                    $"Unknown opcode 0x{(byte)opCode:X2} at 0x{offset:X} in the compiled script.");
        }
    }

    /// <summary>
    /// Reads one operand: a leading kind byte, an address or type index, and whatever that kind
    /// needs after it.
    /// </summary>
    private static IfpsOperand ReadOperand(IfpsReader reader, IReadOnlyList<IfpsType> types)
    {
        var kind = reader.ReadByte();
        var parameter = reader.ReadUInt32();

        switch (kind)
        {
            case 0:
                return new IfpsVariableOperand(new IfpsAddress(parameter));

            case 1:
            {
                if (parameter >= (uint)types.Count)
                    throw new IfpsException(
                        $"An inline constant names type {parameter}, but the script declares only {types.Count} types.");

                var type = types[(int)parameter];
                var setByteSize = type is IfpsSetType set ? set.ByteSize : 0;

                return new IfpsImmediateOperand(parameter, IfpsValue.Read(reader, type.BaseType, setByteSize));
            }

            case 2:
                return new IfpsIndexedOperand(new IfpsAddress(parameter), reader.ReadUInt32());

            case 3:
                return new IfpsVariableIndexedOperand(new IfpsAddress(parameter), new IfpsAddress(reader.ReadUInt32()));

            default:
                throw new IfpsException($"Unknown operand kind {kind} at 0x{reader.Position - 5:X}.");
        }
    }
}
