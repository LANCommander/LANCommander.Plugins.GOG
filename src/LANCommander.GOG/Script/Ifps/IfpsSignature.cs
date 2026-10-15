using System.Globalization;

namespace LANCommander.GOG.Script.Ifps;

/// <summary>
/// The signature an exported bytecode function is published under.
/// </summary>
/// <remarks>
/// Pascal Script stores it as a space-separated list of type indices: the return type first —
/// spelled <c>-1</c> for a procedure — and one token per parameter after it. The prefix is the
/// parameter's mode: <c>@</c> passes it in, <c>!</c> passes it by reference. It is the only place
/// a function's arity is written down, so it is what makes an exported function's arguments
/// nameable.
/// </remarks>
/// <param name="ReturnTypeIndex">Type of the result, or <c>null</c> for a procedure.</param>
/// <param name="Parameters">The parameters, in declaration order.</param>
public sealed record IfpsSignature(uint? ReturnTypeIndex, IReadOnlyList<IfpsParameter> Parameters)
{
    /// <summary>True when the function returns a value.</summary>
    public bool ReturnsValue => ReturnTypeIndex is not null;

    /// <summary>
    /// Parses an export declaration, returning <c>null</c> when it is absent or malformed.
    /// </summary>
    /// <remarks>
    /// A declaration that cannot be parsed is not an error worth failing a whole script over —
    /// the function still disassembles, it just loses its parameter names.
    /// </remarks>
    public static IfpsSignature? Parse(string? declaration)
    {
        if (string.IsNullOrWhiteSpace(declaration))
            return null;

        var tokens = declaration.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
            return null;

        uint? returnType;

        if (tokens[0] == "-1")
            returnType = null;
        else if (uint.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            returnType = parsed;
        else
            return null;

        var parameters = new List<IfpsParameter>(tokens.Length - 1);

        for (var index = 1; index < tokens.Length; index++)
        {
            var token = tokens[index];

            if (token.Length < 2 || token[0] is not ('@' or '!'))
                return null;

            if (!uint.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var typeIndex))
                return null;

            parameters.Add(new IfpsParameter(typeIndex, ByReference: token[0] == '!'));
        }

        return new IfpsSignature(returnType, parameters);
    }
}

/// <summary>One parameter of an <see cref="IfpsSignature"/>.</summary>
/// <param name="TypeIndex">Index of the parameter's type in the script's type table.</param>
/// <param name="ByReference">
/// True for a Pascal <c>var</c> or <c>out</c> parameter. The compiled form does not distinguish
/// the two, nor does it mark <c>const</c>, so a by-value parameter here may have been written
/// <c>const</c> in the source.
/// </param>
public sealed record IfpsParameter(uint TypeIndex, bool ByReference);
