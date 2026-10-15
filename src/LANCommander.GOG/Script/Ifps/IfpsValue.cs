using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace LANCommander.GOG.Script.Ifps;

/// <summary>
/// A constant value stored inside a compiled script, either as an inline instruction operand
/// or as the field of an attribute.
/// </summary>
/// <remarks>
/// The encoded bytes are kept verbatim rather than being widened into a common numeric type.
/// Two of the formats cannot survive that widening — <see cref="IfpsBaseType.Extended"/> is an
/// 80-bit float with no .NET equivalent, and <see cref="IfpsBaseType.U64"/> does not fit in a
/// signed 64-bit integer — and keeping the original bytes is what lets a script be written
/// back out byte for byte.
/// </remarks>
public sealed record IfpsValue
{
    /// <summary>The kind of value stored.</summary>
    public required IfpsBaseType BaseType { get; init; }

    /// <summary>The value exactly as it appears in the blob, including any length prefix.</summary>
    public required ReadOnlyMemory<byte> Encoded { get; init; }

    /// <summary>
    /// The number of bytes a value of <paramref name="baseType"/> occupies, or <c>null</c> when
    /// the length is carried by a prefix in the data itself.
    /// </summary>
    /// <param name="baseType">The kind of value.</param>
    /// <param name="setByteSize">Size of the set, in bytes, when <paramref name="baseType"/> is a set.</param>
    internal static int? FixedSize(IfpsBaseType baseType, int setByteSize) => baseType switch
    {
        IfpsBaseType.U8 or IfpsBaseType.S8 or IfpsBaseType.Char => 1,
        IfpsBaseType.U16 or IfpsBaseType.S16 or IfpsBaseType.WideChar => 2,
        IfpsBaseType.U32 or IfpsBaseType.S32 or IfpsBaseType.Single or IfpsBaseType.ProcPtr => 4,
        IfpsBaseType.S64 or IfpsBaseType.U64 or IfpsBaseType.Double or IfpsBaseType.Currency => 8,
        IfpsBaseType.Extended => 10,
        IfpsBaseType.Set => setByteSize,
        _ => null,
    };

    /// <summary>Reads a constant of the given type.</summary>
    /// <param name="reader">Positioned at the start of the value.</param>
    /// <param name="baseType">The kind of value to read.</param>
    /// <param name="setByteSize">Size of the set, in bytes, when <paramref name="baseType"/> is a set.</param>
    internal static IfpsValue Read(IfpsReader reader, IfpsBaseType baseType, int setByteSize)
    {
        var fixedSize = FixedSize(baseType, setByteSize);

        if (fixedSize is { } size)
            return new IfpsValue { BaseType = baseType, Encoded = reader.ReadBytes(size) };

        var start = reader.Position;

        switch (baseType)
        {
            case IfpsBaseType.String or IfpsBaseType.PChar:
                reader.Skip(checked((int)reader.ReadUInt32()));

                break;

            case IfpsBaseType.WideString or IfpsBaseType.UnicodeString:
                reader.Skip(checked((int)reader.ReadUInt32() * 2));

                break;

            default:
                throw new IfpsException($"A constant of type {baseType} cannot be stored inline.");
        }

        var length = reader.Position - start;

        return new IfpsValue { BaseType = baseType, Encoded = reader.At(start).ReadBytes(length) };
    }

    /// <summary>The value as an unsigned integer, for the integer and character types.</summary>
    /// <exception cref="InvalidOperationException">The value is not an integer.</exception>
    public ulong AsUInt64() => BaseType switch
    {
        IfpsBaseType.U8 or IfpsBaseType.Char => Encoded.Span[0],
        IfpsBaseType.S8 => unchecked((ulong)(sbyte)Encoded.Span[0]),
        IfpsBaseType.U16 or IfpsBaseType.WideChar => BinaryPrimitives.ReadUInt16LittleEndian(Encoded.Span),
        IfpsBaseType.S16 => unchecked((ulong)BinaryPrimitives.ReadInt16LittleEndian(Encoded.Span)),
        IfpsBaseType.U32 or IfpsBaseType.ProcPtr => BinaryPrimitives.ReadUInt32LittleEndian(Encoded.Span),
        IfpsBaseType.S32 => unchecked((ulong)BinaryPrimitives.ReadInt32LittleEndian(Encoded.Span)),
        IfpsBaseType.U64 or IfpsBaseType.S64 or IfpsBaseType.Currency => BinaryPrimitives.ReadUInt64LittleEndian(Encoded.Span),
        _ => throw new InvalidOperationException($"A {BaseType} value is not an integer."),
    };

    /// <summary>The value as a signed integer, for the integer and character types.</summary>
    /// <exception cref="InvalidOperationException">The value is not an integer.</exception>
    public long AsInt64() => unchecked((long)AsUInt64());

    /// <summary>The value as text, for the string and character types.</summary>
    /// <exception cref="InvalidOperationException">The value is not a string.</exception>
    public string AsString() => BaseType switch
    {
        IfpsBaseType.String or IfpsBaseType.PChar =>
            Encoding.Latin1.GetString(Encoded.Span[4..]),
        IfpsBaseType.WideString or IfpsBaseType.UnicodeString =>
            Encoding.Unicode.GetString(Encoded.Span[4..]),
        IfpsBaseType.Char => Encoding.Latin1.GetString(Encoded.Span[..1]),
        IfpsBaseType.WideChar => Encoding.Unicode.GetString(Encoded.Span[..2]),
        _ => throw new InvalidOperationException($"A {BaseType} value is not a string."),
    };

    /// <summary>
    /// The value rendered the way Pascal source would spell it, with strings quoted and
    /// unprintable characters written as <c>#nn</c> escapes.
    /// </summary>
    /// <param name="asBoolean">
    /// Whether a single byte should read as <c>True</c> or <c>False</c>. Pascal Script stores a
    /// Boolean and a Byte the same way, so only the declared type tells them apart.
    /// </param>
    public string ToLiteral(bool asBoolean = true) => BaseType switch
    {
        IfpsBaseType.U8 => Encoded.Span[0] switch
        {
            0 when asBoolean => "False",
            1 when asBoolean => "True",
            var other => other.ToString(CultureInfo.InvariantCulture),
        },
        IfpsBaseType.S8 or IfpsBaseType.U16 or IfpsBaseType.S16 or IfpsBaseType.U32 or IfpsBaseType.S32
            or IfpsBaseType.S64 =>
            AsInt64().ToString(CultureInfo.InvariantCulture),
        IfpsBaseType.U64 =>
            AsUInt64().ToString(CultureInfo.InvariantCulture),
        IfpsBaseType.ProcPtr =>
            $"@Function{AsUInt64().ToString(CultureInfo.InvariantCulture)}",
        IfpsBaseType.Currency =>
            (AsInt64() / 10000m).ToString(CultureInfo.InvariantCulture),
        IfpsBaseType.Single =>
            BinaryPrimitives.ReadSingleLittleEndian(Encoded.Span).ToString("R", CultureInfo.InvariantCulture),
        IfpsBaseType.Double =>
            BinaryPrimitives.ReadDoubleLittleEndian(Encoded.Span).ToString("R", CultureInfo.InvariantCulture),
        IfpsBaseType.Extended =>
            ExtendedToString(Encoded.Span),
        IfpsBaseType.String or IfpsBaseType.PChar or IfpsBaseType.WideString
            or IfpsBaseType.UnicodeString or IfpsBaseType.Char or IfpsBaseType.WideChar =>
            QuotePascal(AsString()),
        IfpsBaseType.Set =>
            $"[${Convert.ToHexString(Encoded.Span)}]",
        _ => $"${Convert.ToHexString(Encoded.Span)}",
    };

    /// <summary>
    /// Converts an 80-bit x87 extended float to the nearest <see cref="double"/>.
    /// </summary>
    /// <remarks>
    /// .NET has no 80-bit float, so this loses the bottom 11 bits of the significand. The
    /// original bytes stay in <see cref="Encoded"/>, so nothing is lost on the way back out.
    /// </remarks>
    private static string ExtendedToString(ReadOnlySpan<byte> bytes)
    {
        var significand = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        var exponentAndSign = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);

        var negative = (exponentAndSign & 0x8000) != 0;
        var exponent = exponentAndSign & 0x7FFF;

        if (significand == 0 && exponent == 0)
            return negative ? "-0" : "0";

        // The x87 format stores the leading 1 explicitly, so the significand is scaled by
        // 2^-63 rather than the 2^-52 an IEEE double uses.
        var value = significand * Math.Pow(2, exponent - 16383 - 63);

        return (negative ? -value : value).ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Quotes a string the way Pascal source does, escaping what cannot be typed.</summary>
    internal static string QuotePascal(string value)
    {
        var builder = new StringBuilder();
        var quoted = false;

        foreach (var character in value)
        {
            if (character < ' ' || character == '\x7F')
            {
                if (quoted)
                {
                    builder.Append('\'');
                    quoted = false;
                }

                builder.Append('#').Append(((int)character).ToString(CultureInfo.InvariantCulture));

                continue;
            }

            if (!quoted)
            {
                builder.Append('\'');
                quoted = true;
            }

            builder.Append(character);

            if (character == '\'')
                builder.Append('\'');
        }

        if (quoted)
            builder.Append('\'');

        return builder.Length == 0 ? "''" : builder.ToString();
    }
}
