using System.Buffers.Binary;
using System.Text;

namespace LANCommander.GOG.Script.Ifps;

/// <summary>
/// A compiled Pascal Script container, decoded into types, functions, globals, and bytecode.
/// </summary>
/// <remarks>
/// <para>
/// This is the format Inno Setup stores its <c>[Code]</c> section in. Inno adds no wrapper of
/// its own: the bytes it hands to the interpreter are exactly the bytes read here.
/// </para>
/// <para>
/// Loading never executes anything. The bytecode is decoded into instructions and left alone.
/// </para>
/// </remarks>
public sealed record IfpsScript
{
    /// <summary>The four bytes every compiled script starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "IFPS"u8;

    /// <summary>The header is the magic plus six 32-bit fields.</summary>
    private const int HeaderLength = 4 + (6 * 4);

    /// <summary>The oldest container version this reader accepts.</summary>
    public const uint LowestVersion = 12;

    /// <summary>The newest container version this reader accepts.</summary>
    public const uint HighestVersion = 23;

    /// <summary>The version from build 21 onwards, where types and functions carry attributes.</summary>
    private const uint FirstVersionWithAttributes = 21;

    /// <summary>The value <see cref="EntryPointIndex"/> takes when the script has no entry point.</summary>
    private const uint NoEntryPoint = 0xFFFFFFFF;

    /// <summary>The container format version, which Pascal Script calls its build number.</summary>
    public required uint Version { get; init; }

    /// <summary>Index of the function the host calls first, or <c>null</c> when there is none.</summary>
    public required uint? EntryPointIndex { get; init; }

    /// <summary>
    /// The import table size recorded in the header. Pascal Script has never used this field, and
    /// every script seen in the wild stores zero, but it is preserved so the header round-trips.
    /// </summary>
    public required uint ImportTableSize { get; init; }

    /// <summary>Every type the script declares, in table order.</summary>
    public required IReadOnlyList<IfpsType> Types { get; init; }

    /// <summary>Every function the script declares or imports, in table order.</summary>
    public required IReadOnlyList<IfpsFunction> Functions { get; init; }

    /// <summary>Every global variable the script declares, in table order.</summary>
    public required IReadOnlyList<IfpsGlobal> Globals { get; init; }

    /// <summary>Absolute offset where the metadata tables end and the bytecode begins.</summary>
    public required int CodeSegmentOffset { get; init; }

    /// <summary>
    /// The bytecode area, verbatim.
    /// </summary>
    /// <remarks>
    /// Function bodies address this area absolutely, so it is kept whole rather than split per
    /// function. Writing it back unchanged is what keeps those offsets valid.
    /// </remarks>
    public required ReadOnlyMemory<byte> CodeSegment { get; init; }

    /// <summary>The function the host calls first, or <c>null</c> when the script has no entry point.</summary>
    public IfpsFunction? EntryPoint =>
        EntryPointIndex is { } index && index < (uint)Functions.Count ? Functions[(int)index] : null;

    /// <summary>The functions implemented in bytecode, in table order.</summary>
    public IEnumerable<IfpsScriptFunction> ScriptFunctions => Functions.OfType<IfpsScriptFunction>();

    /// <summary>The functions the host provides, in table order.</summary>
    public IEnumerable<IfpsExternalFunction> ExternalFunctions => Functions.OfType<IfpsExternalFunction>();

    /// <summary>True when every bytecode function decoded without error.</summary>
    public bool FullyDecoded => ScriptFunctions.All(function => function.DecodeError is null);

    /// <summary>True when the blob begins with the compiled-script magic.</summary>
    public static bool IsCompiledScript(ReadOnlySpan<byte> blob) => blob.StartsWith(Magic);

    /// <summary>
    /// Decodes a compiled script.
    /// </summary>
    /// <param name="blob">The bytes Inno Setup stores in its setup header.</param>
    /// <exception cref="IfpsException">The blob is not a compiled script, or is malformed.</exception>
    public static IfpsScript Load(ReadOnlyMemory<byte> blob)
    {
        if (!IsCompiledScript(blob.Span))
            throw new IfpsException(
                $"The blob does not begin with the compiled-script magic \"IFPS\" (found {Describe(blob.Span)}).");

        var reader = new IfpsReader(blob);

        reader.Skip(Magic.Length);

        var version = reader.ReadUInt32();

        if (version is < LowestVersion or > HighestVersion)
            throw new IfpsException(
                $"Compiled script version {version} is outside the supported range {LowestVersion}-{HighestVersion}.");

        var typeCount = reader.ReadUInt32();
        var functionCount = reader.ReadUInt32();
        var globalCount = reader.ReadUInt32();
        var entryPoint = reader.ReadUInt32();
        var importTableSize = reader.ReadUInt32();

        var types = ReadTypes(reader, typeCount, version);
        var functions = ReadFunctions(reader, functionCount, version, types);
        var globals = ReadGlobals(reader, globalCount, types);

        if (entryPoint != NoEntryPoint && entryPoint >= (uint)functions.Count)
            throw new IfpsException(
                $"The entry point names function {entryPoint}, but the script declares only {functions.Count}.");

        var codeSegmentOffset = reader.Position;

        return new IfpsScript
        {
            Version = version,
            EntryPointIndex = entryPoint == NoEntryPoint ? null : entryPoint,
            ImportTableSize = importTableSize,
            Types = types,
            Functions = DecodeBodies(new IfpsReader(blob), functions, types),
            Globals = globals,
            CodeSegmentOffset = codeSegmentOffset,
            CodeSegment = blob[codeSegmentOffset..],
        };
    }

    private static List<IfpsType> ReadTypes(IfpsReader reader, uint count, uint version)
    {
        var types = new List<IfpsType>(Capacity(count, reader));

        for (var index = 0; index < count; index++)
        {
            var raw = reader.ReadByte();
            var exported = (raw & 0x80) != 0;
            var baseType = (IfpsBaseType)(raw & 0x7F);

            IfpsType type = baseType switch
            {
                IfpsBaseType.Class => new IfpsClassType
                {
                    Index = index,
                    BaseType = baseType,
                    ClassName = ReadCappedString(reader, "a class name"),
                },
                IfpsBaseType.ProcPtr => new IfpsProcedurePointerType
                {
                    Index = index,
                    BaseType = baseType,
                    ParameterInfo = ReadCappedBytes(reader, "a procedure pointer descriptor"),
                },
                IfpsBaseType.Interface => new IfpsInterfaceType
                {
                    Index = index,
                    BaseType = baseType,
                    Guid = reader.ReadBytes(16),
                },
                IfpsBaseType.Set => new IfpsSetType
                {
                    Index = index,
                    BaseType = baseType,
                    BitSize = reader.ReadUInt32(),
                },
                IfpsBaseType.StaticArray => new IfpsStaticArrayType
                {
                    Index = index,
                    BaseType = baseType,
                    ElementTypeIndex = reader.ReadUInt32(),
                    Size = reader.ReadUInt32(),
                    StartOffset = reader.ReadUInt32(),
                },
                IfpsBaseType.Array => new IfpsArrayType
                {
                    Index = index,
                    BaseType = baseType,
                    ElementTypeIndex = reader.ReadUInt32(),
                },
                IfpsBaseType.Record => new IfpsRecordType
                {
                    Index = index,
                    BaseType = baseType,
                    FieldTypeIndices = ReadFieldTypes(reader),
                },
                _ => new IfpsPrimitiveType { Index = index, BaseType = baseType },
            };

            if (exported)
                type = type with { ExportName = reader.ReadLongString() };

            if (version >= FirstVersionWithAttributes)
                type = type with { Attributes = ReadAttributes(reader, types) };

            types.Add(type);
        }

        return types;
    }

    private static List<uint> ReadFieldTypes(IfpsReader reader)
    {
        var fieldCount = reader.ReadUInt32();

        if (fieldCount == 0)
            throw new IfpsException($"A record type at offset {reader.Position - 4} declares no fields.");

        var fields = new List<uint>(Capacity(fieldCount, reader));

        for (var field = 0; field < fieldCount; field++)
            fields.Add(reader.ReadUInt32());

        return fields;
    }

    private static List<IfpsFunction> ReadFunctions(
        IfpsReader reader,
        uint count,
        uint version,
        IReadOnlyList<IfpsType> types)
    {
        var functions = new List<IfpsFunction>(Capacity(count, reader));

        for (var index = 0; index < count; index++)
        {
            var flags = reader.ReadByte();

            IfpsFunction function;

            if ((flags & 1) != 0)
            {
                var name = reader.ReadShortString();

                function = new IfpsExternalFunction
                {
                    Index = index,
                    Flags = flags,
                    Name = name,
                    Declaration = (flags & 3) == 3 ? reader.ReadLongString() : null,
                };
            }
            else
            {
                var codeOffset = reader.ReadUInt32();
                var codeLength = reader.ReadUInt32();

                function = new IfpsScriptFunction
                {
                    Index = index,
                    Flags = flags,
                    CodeOffset = codeOffset,
                    CodeLength = codeLength,
                    ExportName = (flags & 2) != 0 ? reader.ReadLongString() : null,
                    ExportDeclaration = (flags & 2) != 0 ? reader.ReadLongString() : null,
                };
            }

            if ((flags & 4) != 0)
            {
                var attributes = ReadAttributes(reader, types);

                function = function switch
                {
                    IfpsScriptFunction script => script with { Attributes = attributes },
                    IfpsExternalFunction external => external with { Attributes = attributes },
                    _ => function,
                };
            }

            _ = version;

            functions.Add(function);
        }

        return functions;
    }

    private static List<IfpsGlobal> ReadGlobals(IfpsReader reader, uint count, IReadOnlyList<IfpsType> types)
    {
        var globals = new List<IfpsGlobal>(Capacity(count, reader));

        for (var index = 0; index < count; index++)
        {
            var typeIndex = reader.ReadUInt32();
            var flags = reader.ReadByte();

            if (typeIndex >= (uint)types.Count)
                throw new IfpsException(
                    $"Global {index} names type {typeIndex}, but the script declares only {types.Count} types.");

            globals.Add(new IfpsGlobal
            {
                Index = index,
                TypeIndex = typeIndex,
                Flags = flags,
                ExportName = (flags & 1) != 0 ? reader.ReadLongString() : null,
            });
        }

        return globals;
    }

    private static List<IfpsAttribute> ReadAttributes(IfpsReader reader, IReadOnlyList<IfpsType> types)
    {
        var count = reader.ReadUInt32();

        if (count == 0)
            return [];

        var attributes = new List<IfpsAttribute>(Capacity(count, reader));

        for (var index = 0; index < count; index++)
        {
            var name = reader.ReadLongString();
            var fieldCount = reader.ReadUInt32();
            var fields = new List<IfpsAttributeField>(Capacity(fieldCount, reader));

            for (var field = 0; field < fieldCount; field++)
            {
                var typeIndex = reader.ReadUInt32();

                if (typeIndex >= (uint)types.Count)
                    throw new IfpsException(
                        $"Attribute \"{name}\" names type {typeIndex}, which is not declared before it.");

                var type = types[(int)typeIndex];
                var setByteSize = type is IfpsSetType set ? set.ByteSize : 0;

                fields.Add(new IfpsAttributeField(typeIndex, IfpsValue.Read(reader, type.BaseType, setByteSize)));
            }

            attributes.Add(new IfpsAttribute { Name = name, Fields = fields });
        }

        return attributes;
    }

    /// <summary>
    /// Decodes every bytecode body, recording rather than throwing when one fails.
    /// </summary>
    /// <remarks>
    /// A script with one unreadable function is still worth reading: the remaining functions and
    /// the whole type table usually explain what the installer does. The failure is kept on the
    /// function so a disassembly can show where decoding stopped.
    /// </remarks>
    private static List<IfpsFunction> DecodeBodies(
        IfpsReader reader,
        List<IfpsFunction> functions,
        IReadOnlyList<IfpsType> types)
    {
        for (var index = 0; index < functions.Count; index++)
        {
            if (functions[index] is not IfpsScriptFunction function)
                continue;

            var instructions = new List<IfpsInstruction>();
            string? error = null;

            try
            {
                InstructionDecoder.Decode(reader, function.CodeOffset, function.CodeLength, types, instructions);
            }
            catch (IfpsException exception)
            {
                error = exception.Message;
            }

            functions[index] = function with { Instructions = instructions, DecodeError = error };
        }

        return functions;
    }

    /// <summary>
    /// Caps a declared count to what the remaining bytes could possibly hold.
    /// </summary>
    /// <remarks>
    /// A corrupt count would otherwise make the loader pre-allocate gigabytes before reaching the
    /// read that fails. Every record is at least one byte, so the remaining length is a safe bound.
    /// </remarks>
    private static int Capacity(uint count, IfpsReader reader) => (int)Math.Min(count, (uint)reader.Remaining);

    private static string ReadCappedString(IfpsReader reader, string what)
    {
        var length = reader.ReadUInt32();

        if (length > 255)
            throw new IfpsException($"The script declares {what} of {length} bytes, which the format caps at 255.");

        return Encoding.Latin1.GetString(reader.ReadBytes((int)length));
    }

    private static byte[] ReadCappedBytes(IfpsReader reader, string what)
    {
        var length = reader.ReadUInt32();

        if (length > 255)
            throw new IfpsException($"The script declares {what} of {length} bytes, which the format caps at 255.");

        return reader.ReadBytes((int)length);
    }

    private static string Describe(ReadOnlySpan<byte> blob) =>
        blob.Length == 0
            ? "an empty blob"
            : $"0x{Convert.ToHexString(blob[..Math.Min(4, blob.Length)])}";

    /// <summary>The header, needed by the writer and kept beside the reader that consumes it.</summary>
    internal void WriteHeader(Span<byte> destination)
    {
        Magic.CopyTo(destination);

        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)Types.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], (uint)Functions.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], (uint)Globals.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], EntryPointIndex ?? NoEntryPoint);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], ImportTableSize);
    }

    /// <summary>The size of the fixed header, for the writer.</summary>
    internal static int HeaderSize => HeaderLength;

    /// <summary>Whether this container version stores attributes on every type.</summary>
    internal bool StoresTypeAttributes => Version >= FirstVersionWithAttributes;
}
