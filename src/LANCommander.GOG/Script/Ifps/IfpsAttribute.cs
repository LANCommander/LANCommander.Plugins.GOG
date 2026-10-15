namespace LANCommander.GOG.Script.Ifps;

/// <summary>
/// An attribute attached to a type or function, carrying a name and a list of typed values.
/// </summary>
/// <remarks>
/// Attributes appear only in containers built at version 21 or later. Inno Setup's compiler
/// does not emit them, so in practice every GOG installer carries empty attribute lists —
/// but the counts are still written, and skipping them would desynchronise the reader.
/// </remarks>
public sealed record IfpsAttribute
{
    /// <summary>The attribute's type name.</summary>
    public required string Name { get; init; }

    /// <summary>The attribute's field values, in declaration order.</summary>
    public required IReadOnlyList<IfpsAttributeField> Fields { get; init; }
}

/// <summary>One field of an <see cref="IfpsAttribute"/>.</summary>
/// <param name="TypeIndex">Index of the field's type in the script's type table.</param>
/// <param name="Value">The stored value.</param>
public sealed record IfpsAttributeField(uint TypeIndex, IfpsValue Value);
