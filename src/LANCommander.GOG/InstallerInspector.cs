using LANCommander.GOG.Inno.Loader;
using LANCommander.GOG.Inno.Streams;
using LANCommander.GOG.Inno.Versioning;

namespace LANCommander.GOG;

/// <summary>
/// A cheap description of an installer, obtained without decoding its full header or
/// touching its payload. Intended for scanning a large library of files.
/// </summary>
/// <param name="Path">Absolute path to the installer executable.</param>
/// <param name="Version">The Inno Setup data-format version.</param>
/// <param name="UsesExternalSlices">True when the payload lives in <c>-N.bin</c> files.</param>
/// <param name="HeaderOffset">Offset of the setup header block within the executable.</param>
/// <param name="PayloadOffset">Offset of the embedded payload, or 0 when sliced.</param>
/// <param name="LoaderSignature">The 12 loader signature bytes, which identify the Inno fork.</param>
public sealed record InstallerIdentity(
    string Path,
    InnoVersion Version,
    bool UsesExternalSlices,
    long HeaderOffset,
    long PayloadOffset,
    byte[] LoaderSignature)
{
    /// <summary>The loader signature rendered as lowercase hex, for diagnostics.</summary>
    public string LoaderSignatureHex => Convert.ToHexStringLower(LoaderSignature);
}

/// <summary>
/// Identifies GOG/Inno Setup installers without parsing or extracting them.
/// </summary>
public static class InstallerInspector
{
    /// <summary>
    /// Reads just enough of <paramref name="path"/> to report its format and layout.
    /// </summary>
    /// <exception cref="UnsupportedInstallerException">The file is not an Inno Setup installer.</exception>
    public static InstallerIdentity Identify(string path)
    {
        using var stream = File.OpenRead(path);

        var offsets = SetupLoader.Locate(stream);

        stream.Position = offsets.HeaderOffset;

        var identifier = new byte[InnoVersion.IdentifierLength];

        if (stream.ReadAtLeast(identifier, identifier.Length, throwOnEndOfStream: false) < identifier.Length)
            throw new CorruptInstallerException("Installer ends before its setup header identifier.");

        return new InstallerIdentity(
            System.IO.Path.GetFullPath(path),
            InnoVersion.Parse(identifier),
            offsets.UsesExternalSlices,
            offsets.HeaderOffset,
            offsets.PayloadOffset,
            offsets.Signature);
    }

    /// <summary>
    /// Attempts to identify <paramref name="path"/>, returning <see langword="false"/>
    /// rather than throwing when it is not a supported installer.
    /// </summary>
    public static bool TryIdentify(string path, out InstallerIdentity? identity)
    {
        try
        {
            identity = Identify(path);

            return true;
        }
        catch (Exception exception) when (exception is GogInstallerException or IOException)
        {
            identity = null;

            return false;
        }
    }
}
