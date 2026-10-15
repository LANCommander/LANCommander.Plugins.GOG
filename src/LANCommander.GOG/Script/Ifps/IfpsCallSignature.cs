namespace LANCommander.GOG.Script.Ifps;

/// <summary>
/// What a call site has to put on the stack to call a function: one slot per parameter, plus one
/// for the result when there is one.
/// </summary>
/// <remarks>
/// <para>
/// Pascal Script records this four different ways depending on how the function was declared, and
/// this type is the single view over all of them. It is what makes a run of pushes followed by a
/// <see cref="IfpsOpCode.Call"/> readable as a call with arguments.
/// </para>
/// <para>
/// The caller pushes parameters in reverse declaration order and the result slot last, so the
/// topmost stack entry at the call is the result for a function and the first parameter for a
/// procedure.
/// </para>
/// </remarks>
/// <param name="ReturnsValue">True when the call reserves a slot for a result.</param>
/// <param name="ParametersByReference">One entry per parameter, true when it is passed by reference.</param>
/// <param name="HasImplicitSelf">
/// True for a method, which takes the object it is called on as a slot the declaration does not
/// mention.
/// </param>
public sealed record IfpsCallSignature(
    bool ReturnsValue,
    IReadOnlyList<bool> ParametersByReference,
    bool HasImplicitSelf = false)
{
    /// <summary>How many stack slots the call consumes.</summary>
    public int SlotCount =>
        ParametersByReference.Count + (ReturnsValue ? 1 : 0) + (HasImplicitSelf ? 1 : 0);

    /// <summary>
    /// Reads the packed form Pascal Script uses for imports: a result flag followed by one byte per
    /// parameter, non-zero when the parameter is passed by reference.
    /// </summary>
    internal static IfpsCallSignature? FromPackedModes(ReadOnlySpan<char> packed, bool hasImplicitSelf = false)
    {
        if (packed.Length == 0)
            return null;

        var parameters = new bool[packed.Length - 1];

        for (var index = 0; index < parameters.Length; index++)
            parameters[index] = packed[index + 1] != '\0';

        return new IfpsCallSignature(packed[0] != '\0', parameters, hasImplicitSelf);
    }
}
