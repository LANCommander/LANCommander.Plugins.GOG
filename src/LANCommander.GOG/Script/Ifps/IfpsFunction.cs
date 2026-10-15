namespace LANCommander.GOG.Script.Ifps;

/// <summary>
/// A function declared by a compiled script, either implemented in bytecode or imported from
/// the host that runs it.
/// </summary>
public abstract record IfpsFunction
{
    /// <summary>Position of this function in the script's function table.</summary>
    public required int Index { get; init; }

    /// <summary>The raw flags byte, preserved so the record can be written back unchanged.</summary>
    public required byte Flags { get; init; }

    /// <summary>Attributes attached to the function.</summary>
    public IReadOnlyList<IfpsAttribute> Attributes { get; init; } = [];

    /// <summary>True when the host, rather than the script, provides the implementation.</summary>
    public bool IsExternal => (Flags & 1) != 0;

    /// <summary>True when the function is visible under a name outside the script.</summary>
    public bool IsExported => (Flags & 2) != 0;

    /// <summary>True when an attribute list follows the function record.</summary>
    public bool HasAttributes => (Flags & 4) != 0;

    /// <summary>A readable name for the function, falling back to a positional one when unnamed.</summary>
    public abstract string DisplayName { get; }

    /// <summary>
    /// What a call site must push to call this function, or <c>null</c> when the container does not
    /// say. A caller with no signature has to fall back to counting the pops that follow the call.
    /// </summary>
    public abstract IfpsCallSignature? CallSignature { get; }
}

/// <summary>A function implemented in the script's own bytecode.</summary>
public sealed record IfpsScriptFunction : IfpsFunction
{
    /// <summary>Absolute offset of the function's bytecode within the compiled blob.</summary>
    public required uint CodeOffset { get; init; }

    /// <summary>Length of the function's bytecode, in bytes.</summary>
    public required uint CodeLength { get; init; }

    /// <summary>The name the script exports the function under, when it is exported.</summary>
    public string? ExportName { get; init; }

    /// <summary>
    /// The exported signature, when the function is exported.
    /// </summary>
    /// <remarks>
    /// The first token is the return type — <c>-1</c> for a procedure — and each token after it
    /// describes one parameter, prefixed with <c>@</c> when it is passed by reference.
    /// </remarks>
    public string? ExportDeclaration { get; init; }

    /// <summary>
    /// The parsed form of <see cref="ExportDeclaration"/>, or <c>null</c> when the function is not
    /// exported or its declaration could not be read.
    /// </summary>
    public IfpsSignature? Signature => IfpsSignature.Parse(ExportDeclaration);

    /// <summary>The decoded body, or an empty list when <see cref="DecodeError"/> is set.</summary>
    public IReadOnlyList<IfpsInstruction> Instructions { get; init; } = [];

    /// <summary>
    /// Why the body could not be decoded, or <c>null</c> when it decoded cleanly.
    /// </summary>
    /// <remarks>
    /// A single unreadable function does not prevent the rest of the script from loading; the
    /// failure is recorded here so a disassembly can show exactly how far it got.
    /// </remarks>
    public string? DecodeError { get; init; }

    /// <inheritdoc />
    public override string DisplayName => ExportName ?? $"Function{Index}";

    /// <inheritdoc />
    public override IfpsCallSignature? CallSignature =>
        Signature is { } signature
            ? new IfpsCallSignature(
                signature.ReturnsValue,
                signature.Parameters.Select(parameter => parameter.ByReference).ToArray())
            : null;
}

/// <summary>How an external function reaches its implementation.</summary>
public enum IfpsImportKind
{
    /// <summary>Registered by name with the host, which is how Inno Setup exposes its script API.</summary>
    Internal,

    /// <summary>A method or property accessor on a registered class.</summary>
    Class,

    /// <summary>An entry point in a native DLL.</summary>
    Dll,

    /// <summary>A form this reader does not recognise.</summary>
    Unknown,
}

/// <summary>A function the host provides rather than the script.</summary>
public sealed record IfpsExternalFunction : IfpsFunction
{
    /// <summary>
    /// The name the host registered the function under, or an empty string when the function is
    /// identified by <see cref="Declaration"/> instead.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The import declaration, present only when the function carries one.
    /// </summary>
    /// <remarks>
    /// A class import reads <c>class:CLASSNAME|METHODNAME|…</c>; a native import begins with
    /// <c>dll:</c>. Both are kept verbatim because the trailing bytes encode calling convention
    /// and parameter kinds as raw characters.
    /// </remarks>
    public string? Declaration { get; init; }

    /// <summary>Where the implementation comes from.</summary>
    /// <remarks>
    /// Only the class and native forms announce themselves with a prefix. Everything else is a
    /// function the host registered by name — which is how Inno Setup exposes its entire script
    /// API — and carries at most an opaque parameter descriptor.
    /// </remarks>
    public IfpsImportKind Kind =>
        Declaration is null ? IfpsImportKind.Internal
        : Declaration.StartsWith("class:", StringComparison.Ordinal) ? IfpsImportKind.Class
        : Declaration.StartsWith("dll:", StringComparison.Ordinal) ? IfpsImportKind.Dll
        : Name.Length > 0 ? IfpsImportKind.Internal
        : IfpsImportKind.Unknown;

    /// <summary>
    /// The library and entry point a native import names, or <c>null</c> for any other kind.
    /// </summary>
    /// <remarks>
    /// A native declaration is <c>dll:</c> followed by the library name, the entry point, and a
    /// packed calling convention, each separated by a NUL byte.
    /// </remarks>
    public (string Library, string EntryPoint)? NativeImport
    {
        get
        {
            if (Kind != IfpsImportKind.Dll)
                return null;

            var parts = Declaration!["dll:".Length..].Split('\0');

            return parts.Length >= 2 ? (parts[0], parts[1]) : null;
        }
    }

    /// <summary>
    /// The class a class import belongs to, or <c>null</c> for any other kind.
    /// </summary>
    public string? ClassName => Kind == IfpsImportKind.Class ? DeclarationPart(0) : null;

    /// <summary>
    /// The method a class import names, with the trailing <c>@</c> of a property setter removed,
    /// or <c>null</c> for any other kind.
    /// </summary>
    public string? MethodName => Kind == IfpsImportKind.Class ? DeclarationPart(1)?.TrimEnd('@') : null;

    /// <summary>Splits the part of a class declaration that precedes the packed calling convention.</summary>
    private string? DeclarationPart(int index)
    {
        var parts = Declaration!["class:".Length..].Split('|');

        return index < parts.Length ? parts[index] : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Each import kind packs the same information differently: a class declaration puts the
    /// calling convention before the result flag, a native one puts three bytes there, and a
    /// host function registered by name puts nothing.
    /// </remarks>
    public override IfpsCallSignature? CallSignature
    {
        get
        {
            if (Declaration is not { Length: > 0 } declaration)
                return null;

            switch (Kind)
            {
                case IfpsImportKind.Class:
                {
                    var separator = declaration.LastIndexOf('|');

                    // The calling convention sits between the method name and the result flag,
                    // and the object the method runs on is a slot the declaration never mentions.
                    return separator < 0
                        ? null
                        : IfpsCallSignature.FromPackedModes(
                            declaration.AsSpan(separator + 1 + 1),
                            hasImplicitSelf: true);
                }

                case IfpsImportKind.Dll:
                {
                    // dll:Library#0EntryPoint#0 then calling convention, delay load, search path.
                    var entryPoint = declaration.IndexOf('\0', "dll:".Length);
                    var tail = entryPoint < 0 ? -1 : declaration.IndexOf('\0', entryPoint + 1);

                    return tail < 0 || tail + 4 > declaration.Length
                        ? null
                        : IfpsCallSignature.FromPackedModes(declaration.AsSpan(tail + 1 + 3));
                }

                default:
                    return IfpsCallSignature.FromPackedModes(declaration.AsSpan());
            }
        }
    }

    /// <inheritdoc />
    public override string DisplayName =>
        Kind == IfpsImportKind.Class && ClassName is { Length: > 0 } && MethodName is { Length: > 0 }
            ? $"{ClassName}.{MethodName}"
            : NativeImport is { } native ? $"{native.Library}!{native.EntryPoint}"
            : Name.Length > 0 ? Name
            : $"Function{Index}";
}
