namespace LANCommander.GOG.Inno.Loader;

/// <summary>
/// Locates the Inno Setup loader offset table inside an installer executable.
/// </summary>
internal static class SetupLoader
{
    /// <summary>
    /// How much of the file to sweep before falling back to a full scan. The table lives
    /// in the loader stub, which is a few hundred kilobytes at most, so this covers every
    /// real installer without reading gigabytes of payload.
    /// </summary>
    private const int StubScanLimit = 8 * 1024 * 1024;

    private const int ScanBufferSize = 1 << 20;

    /// <summary>
    /// Scans <paramref name="stream"/> for the offset table and returns the first
    /// structurally valid candidate.
    /// </summary>
    /// <exception cref="UnsupportedInstallerException">No valid table was found.</exception>
    public static SetupLoaderOffsets Locate(Stream stream)
    {
        var length = stream.Length;

        return Scan(stream, 0, Math.Min(length, StubScanLimit), length)
            ?? Scan(stream, StubScanLimit, length, length)
            ?? throw new UnsupportedInstallerException(
                "No Inno Setup loader offset table found. This does not look like a GOG/Inno Setup installer.");
    }

    private static SetupLoaderOffsets? Scan(Stream stream, long from, long to, long fileLength)
    {
        if (from >= to)
            return null;

        // Overlap successive buffers so a table straddling a boundary is still matched.
        var overlap = SetupLoaderOffsets.TableLength;
        var buffer = new byte[ScanBufferSize + overlap];
        var position = from;

        while (position < to)
        {
            stream.Position = position;

            var wanted = (int)Math.Min(buffer.Length, fileLength - position);
            var read = stream.ReadAtLeast(buffer.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);

            if (read < SetupLoaderOffsets.TableLength)
                return null;

            var window = buffer.AsSpan(0, read);
            var searchLimit = read - SetupLoaderOffsets.TableLength;

            for (var i = 0; i <= searchLimit; i++)
            {
                var candidate = window[i..];

                if (!candidate.StartsWith(SetupLoaderOffsets.SignaturePrefix))
                    continue;

                var offsets = SetupLoaderOffsets.Read(candidate);

                if (offsets.IsPlausible(fileLength))
                    return offsets;
            }

            position += ScanBufferSize;
        }

        return null;
    }
}
