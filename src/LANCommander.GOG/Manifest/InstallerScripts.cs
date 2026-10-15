using System.Text;

namespace LANCommander.GOG.Manifest;

/// <summary>
/// The custom logic an installer carries, captured as data.
/// </summary>
/// <remarks>
/// Inno Setup compiles an installer's Pascal script to bytecode before packaging, so the
/// original source is not present. The bytecode is preserved verbatim in
/// <see cref="CompiledCode"/> for inspection; this library never executes it.
/// </remarks>
public sealed record InstallerScripts
{
    /// <summary>Magic that introduces a compiled Inno Setup script.</summary>
    private static ReadOnlySpan<byte> CompiledCodeMagic => "IFPS"u8;

    /// <summary>The compiled script bytecode, or empty when the installer has no script.</summary>
    public required byte[] CompiledCode { get; init; }

    /// <summary>Licence text shown before installation.</summary>
    public required string LicenseText { get; init; }

    /// <summary>Informational text shown before installation.</summary>
    public required string InfoBeforeText { get; init; }

    /// <summary>Informational text shown after installation.</summary>
    public required string InfoAfterText { get; init; }

    /// <summary>
    /// Script and metadata files carried in the payload, such as GOG's
    /// <c>goggame-&lt;id&gt;.script</c> and <c>goggame-&lt;id&gt;.info</c>.
    /// </summary>
    /// <remarks>
    /// Populated on demand by <see cref="GogInstaller.LoadPayloadScriptsAsync"/>, because
    /// reading them means decompressing part of the payload.
    /// </remarks>
    public IReadOnlyList<PayloadScript> PayloadScripts { get; init; } = [];

    /// <summary>True when the installer carries compiled script code.</summary>
    public bool HasCompiledCode => CompiledCode.Length > 0;

    /// <summary>
    /// True when <see cref="CompiledCode"/> carries the expected compiled-script magic.
    /// A false result means the blob is present but in an unfamiliar form.
    /// </summary>
    public bool CompiledCodeIsRecognised =>
        CompiledCode.AsSpan().StartsWith(CompiledCodeMagic);
}

/// <summary>
/// A script or metadata file recovered from the installer's payload.
/// </summary>
/// <param name="DestinationPath">Where the installer would write the file.</param>
/// <param name="Content">The file's raw bytes.</param>
public sealed record PayloadScript(string DestinationPath, byte[] Content)
{
    /// <summary>The file name without its directory.</summary>
    public string FileName => InnoPath.GetFileName(DestinationPath);

    /// <summary>The content decoded as UTF-8, with any byte-order mark removed.</summary>
    public string Text => new UTF8Encoding(false).GetString(Content).TrimStart('﻿');
}
