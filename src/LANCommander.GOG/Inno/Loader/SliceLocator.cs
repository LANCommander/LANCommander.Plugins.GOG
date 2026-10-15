using System.Globalization;

namespace LANCommander.GOG.Inno.Loader;

/// <summary>
/// Resolves where an installer's compressed payload physically lives.
/// </summary>
internal static class SliceLocator
{
    /// <summary>
    /// Builds the ordered list of slices backing the payload.
    /// </summary>
    /// <param name="installerPath">Path to the installer executable.</param>
    /// <param name="offsets">The parsed loader offset table.</param>
    /// <exception cref="MissingSliceException">A slice in the sequence is absent.</exception>
    /// <exception cref="UnsupportedPayloadException">A slice is not in Inno's slice format.</exception>
    public static IReadOnlyList<InstallerSlice> Resolve(string installerPath, SetupLoaderOffsets offsets)
    {
        var full = Path.GetFullPath(installerPath);

        if (!offsets.UsesExternalSlices)
        {
            var length = new FileInfo(full).Length;

            return [new InstallerSlice(full, offsets.PayloadOffset, length, Number: 0, IsExternal: false)];
        }

        var directory = Path.GetDirectoryName(full) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(full);
        var slices = new List<InstallerSlice>();

        for (var number = 1; ; number++)
        {
            var path = Path.Combine(directory, $"{stem}-{number.ToString(CultureInfo.InvariantCulture)}.bin");

            if (!File.Exists(path))
            {
                // At least one slice is mandatory; past that, absence simply ends the set.
                if (number == 1)
                    throw new MissingSliceException(
                        $"Installer \"{Path.GetFileName(full)}\" stores its payload in external slices, " +
                        $"but \"{Path.GetFileName(path)}\" was not found beside it.",
                        path);

                break;
            }

            // Files are named from one, but the file table numbers slices from zero.
            slices.Add(Describe(path, number - 1));
        }

        return slices;
    }

    private static InstallerSlice Describe(string path, int number)
    {
        var info = new FileInfo(path);

        using var stream = File.OpenRead(path);

        Span<byte> header = stackalloc byte[InstallerSlice.SliceHeaderLength];

        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            throw new CorruptInstallerException($"Slice \"{Path.GetFileName(path)}\" is truncated.");

        if (!header.StartsWith(InstallerSlice.SliceMagic))
            throw new UnsupportedPayloadException(
                $"Slice \"{Path.GetFileName(path)}\" is not an Inno Setup payload slice. " +
                "Installers that wrap their payload in another container (such as the RAR archives " +
                "used by some pre-2013 GOG builds) are not supported.");

        return new InstallerSlice(
            info.FullName,
            InstallerSlice.SliceHeaderLength,
            info.Length,
            number,
            IsExternal: true);
    }
}
