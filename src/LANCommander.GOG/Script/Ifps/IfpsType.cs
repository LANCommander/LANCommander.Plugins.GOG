namespace LANCommander.GOG.Script.Ifps;

/// <summary>
/// A type declared by a compiled script.
/// </summary>
/// <remarks>
/// Types are referenced by index throughout the container, so their order is significant and
/// <see cref="Index"/> is the identity the rest of the format uses.
/// </remarks>
public abstract record IfpsType
{
    /// <summary>Position of this type in the script's type table.</summary>
    public required int Index { get; init; }

    /// <summary>The kind of type this is.</summary>
    public required IfpsBaseType BaseType { get; init; }

    /// <summary>The name the script exports this type under, or <c>null</c> when it is anonymous.</summary>
    public string? ExportName { get; init; }

    /// <summary>Attributes attached to the type. Present only from container build 21 onwards.</summary>
    public IReadOnlyList<IfpsAttribute> Attributes { get; init; } = [];

    /// <summary>A readable name for the type, falling back to a positional one when unnamed.</summary>
    public virtual string DisplayName => ExportName ?? $"Type{Index}";
}

/// <summary>A type with no structure of its own, such as an integer, float, or string.</summary>
public sealed record IfpsPrimitiveType : IfpsType
{
    /// <inheritdoc />
    public override string DisplayName => ExportName ?? PascalName;

    /// <summary>The name Pascal source would use for this primitive.</summary>
    public string PascalName => BaseType switch
    {
        IfpsBaseType.U8 => "Byte",
        IfpsBaseType.S8 => "ShortInt",
        IfpsBaseType.U16 => "Word",
        IfpsBaseType.S16 => "SmallInt",
        IfpsBaseType.U32 => "Cardinal",
        IfpsBaseType.S32 => "Integer",
        IfpsBaseType.S64 => "Int64",
        IfpsBaseType.U64 => "UInt64",
        IfpsBaseType.Single => "Single",
        IfpsBaseType.Double => "Double",
        IfpsBaseType.Extended => "Extended",
        IfpsBaseType.Currency => "Currency",
        IfpsBaseType.String => "AnsiString",
        IfpsBaseType.PChar => "PChar",
        IfpsBaseType.Char => "AnsiChar",
        IfpsBaseType.WideString => "WideString",
        IfpsBaseType.WideChar => "WideChar",
        IfpsBaseType.UnicodeString => "String",
        IfpsBaseType.Variant or IfpsBaseType.NotificationVariant => "Variant",
        IfpsBaseType.Pointer => "Pointer",
        _ => BaseType.ToString(),
    };
}

/// <summary>A class reference, identified by the class name the host registered it under.</summary>
public sealed record IfpsClassType : IfpsType
{
    /// <summary>The registered class name, such as <c>TWizardForm</c>.</summary>
    public required string ClassName { get; init; }

    /// <inheritdoc />
    public override string DisplayName => ExportName ?? ClassName;
}

/// <summary>A procedure pointer, described by an opaque parameter-info blob.</summary>
public sealed record IfpsProcedurePointerType : IfpsType
{
    /// <summary>
    /// The parameter descriptor, as stored. The first byte is non-zero when the pointer
    /// returns a value; each byte after it is non-zero for a <c>var</c> parameter.
    /// </summary>
    public required ReadOnlyMemory<byte> ParameterInfo { get; init; }

    /// <summary>True when the pointer refers to a function rather than a procedure.</summary>
    public bool ReturnsValue => ParameterInfo.Length > 0 && ParameterInfo.Span[0] != 0;

    /// <summary>The number of parameters the pointer takes.</summary>
    public int ParameterCount => Math.Max(0, ParameterInfo.Length - 1);
}

/// <summary>A COM interface, identified by its GUID.</summary>
public sealed record IfpsInterfaceType : IfpsType
{
    /// <summary>The interface identifier, as sixteen raw bytes in Windows GUID layout.</summary>
    public required ReadOnlyMemory<byte> Guid { get; init; }

    /// <inheritdoc />
    public override string DisplayName => ExportName ?? $"Interface{Index}";
}

/// <summary>A Pascal set, described by the number of bits it spans.</summary>
public sealed record IfpsSetType : IfpsType
{
    /// <summary>How many distinct members the set can hold.</summary>
    public required uint BitSize { get; init; }

    /// <summary>How many bytes the set occupies when stored.</summary>
    public int ByteSize => (int)((BitSize + 7) / 8);
}

/// <summary>A dynamic array.</summary>
public sealed record IfpsArrayType : IfpsType
{
    /// <summary>Index of the element type in the script's type table.</summary>
    public required uint ElementTypeIndex { get; init; }
}

/// <summary>A fixed-length array.</summary>
public sealed record IfpsStaticArrayType : IfpsType
{
    /// <summary>Index of the element type in the script's type table.</summary>
    public required uint ElementTypeIndex { get; init; }

    /// <summary>The number of elements.</summary>
    public required uint Size { get; init; }

    /// <summary>The index the array starts at, which Pascal allows to be any integer.</summary>
    public required uint StartOffset { get; init; }
}

/// <summary>A record, described by the types of its fields in declaration order.</summary>
public sealed record IfpsRecordType : IfpsType
{
    /// <summary>Indices into the script's type table, one per field.</summary>
    public required IReadOnlyList<uint> FieldTypeIndices { get; init; }
}
