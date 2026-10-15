namespace LANCommander.GOG.Script.Ifps;

/// <summary>A global variable declared by a compiled script.</summary>
public sealed record IfpsGlobal
{
    /// <summary>Position of this variable in the script's global table.</summary>
    public required int Index { get; init; }

    /// <summary>Index of the variable's type in the script's type table.</summary>
    public required uint TypeIndex { get; init; }

    /// <summary>The raw flags byte, preserved so the record can be written back unchanged.</summary>
    public required byte Flags { get; init; }

    /// <summary>The name the script exports the variable under, or <c>null</c> when it is anonymous.</summary>
    public string? ExportName { get; init; }

    /// <summary>True when the variable is visible under a name outside the script.</summary>
    public bool IsExported => (Flags & 1) != 0;

    /// <summary>A readable name for the variable, falling back to a positional one when unnamed.</summary>
    public string DisplayName => ExportName ?? $"Global{Index}";
}
