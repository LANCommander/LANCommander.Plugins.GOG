namespace LANCommander.GOG.Script.Ifps;

/// <summary>
/// The primitive kinds a Pascal Script type can be built from.
/// </summary>
/// <remarks>
/// These are the <c>bt*</c> constants from Pascal Script's <c>uPSUtils.pas</c>. Values above
/// 128 exist only inside the compiler: in a compiled script the high bit of the type byte is
/// the "has an exported name" flag, so <see cref="Enum"/>, <see cref="Type"/>, and
/// <see cref="ExternalClass"/> can never appear in a loaded container.
/// </remarks>
public enum IfpsBaseType : byte
{
    /// <summary>A saved return address. Internal to the interpreter.</summary>
    ReturnAddress = 0,

    /// <summary>Unsigned 8-bit integer, and the type Pascal Script uses for <c>Boolean</c>.</summary>
    U8 = 1,

    /// <summary>Signed 8-bit integer.</summary>
    S8 = 2,

    /// <summary>Unsigned 16-bit integer.</summary>
    U16 = 3,

    /// <summary>Signed 16-bit integer.</summary>
    S16 = 4,

    /// <summary>Unsigned 32-bit integer.</summary>
    U32 = 5,

    /// <summary>Signed 32-bit integer.</summary>
    S32 = 6,

    /// <summary>32-bit IEEE float.</summary>
    Single = 7,

    /// <summary>64-bit IEEE float.</summary>
    Double = 8,

    /// <summary>80-bit extended float, stored as ten bytes.</summary>
    Extended = 9,

    /// <summary>Length-prefixed 8-bit string.</summary>
    String = 10,

    /// <summary>A record, described by the types of its fields.</summary>
    Record = 11,

    /// <summary>A dynamic array.</summary>
    Array = 12,

    /// <summary>An untyped pointer.</summary>
    Pointer = 13,

    /// <summary>A null-terminated 8-bit string.</summary>
    PChar = 14,

    /// <summary>A resource pointer. Internal to the interpreter.</summary>
    ResourcePointer = 15,

    /// <summary>An OLE variant.</summary>
    Variant = 16,

    /// <summary>Signed 64-bit integer.</summary>
    S64 = 17,

    /// <summary>A single 8-bit character.</summary>
    Char = 18,

    /// <summary>A UTF-16 string, stored with a character-count prefix.</summary>
    WideString = 19,

    /// <summary>A single UTF-16 character.</summary>
    WideChar = 20,

    /// <summary>A procedure pointer, described by a parameter-info blob.</summary>
    ProcPtr = 21,

    /// <summary>A fixed-length array.</summary>
    StaticArray = 22,

    /// <summary>A Pascal set, described by its size in bits.</summary>
    Set = 23,

    /// <summary>A fixed-point currency value, stored as a scaled 64-bit integer.</summary>
    Currency = 24,

    /// <summary>A class reference, described by its class name.</summary>
    Class = 25,

    /// <summary>A COM interface, described by its GUID.</summary>
    Interface = 26,

    /// <summary>A variant carrying change notifications.</summary>
    NotificationVariant = 27,

    /// <summary>A UTF-16 string, stored with a character-count prefix.</summary>
    UnicodeString = 28,

    /// <summary>Unsigned 64-bit integer.</summary>
    U64 = 29,

    /// <summary>Compiler-only: an enumeration.</summary>
    Enum = 129,

    /// <summary>Compiler-only: a type reference.</summary>
    Type = 130,

    /// <summary>Compiler-only: an external class.</summary>
    ExternalClass = 131,
}
