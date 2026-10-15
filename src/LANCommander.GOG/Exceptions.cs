namespace LANCommander.GOG;

/// <summary>Base type for every failure raised while reading a GOG installer.</summary>
public class GogInstallerException : Exception
{
    /// <summary>Creates the exception with a message describing the failure.</summary>
    public GogInstallerException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public GogInstallerException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The file is not a GOG/Inno Setup installer, or uses a format this library cannot read.</summary>
public sealed class UnsupportedInstallerException : GogInstallerException
{
    /// <summary>Creates the exception with a message describing what was found instead.</summary>
    public UnsupportedInstallerException(string message) : base(message) { }
}

/// <summary>The installer is an Inno Setup package whose data-format version is not supported.</summary>
public sealed class UnsupportedVersionException : GogInstallerException
{
    /// <summary>Creates the exception with a message naming the unsupported version.</summary>
    public UnsupportedVersionException(string message) : base(message) { }

    /// <summary>The version that was detected, when it could be parsed.</summary>
    public Inno.Versioning.InnoVersion? DetectedVersion { get; init; }
}

/// <summary>
/// The payload is stored in a container this library does not handle, such as the RAR
/// payloads used by some pre-2013 GOG installers.
/// </summary>
public sealed class UnsupportedPayloadException : GogInstallerException
{
    /// <summary>Creates the exception with a message describing the payload container.</summary>
    public UnsupportedPayloadException(string message) : base(message) { }
}

/// <summary>A required <c>-N.bin</c> slice could not be found next to the installer.</summary>
public sealed class MissingSliceException : GogInstallerException
{
    /// <summary>Creates the exception, recording which slice was expected.</summary>
    /// <param name="message">A message describing what is missing.</param>
    /// <param name="expectedPath">The path the missing slice was expected at.</param>
    public MissingSliceException(string message, string expectedPath) : base(message)
        => ExpectedPath = expectedPath;

    /// <summary>The path the missing slice was expected at.</summary>
    public string ExpectedPath { get; }
}

/// <summary>A checksum or structural invariant failed, indicating a truncated or damaged installer.</summary>
public sealed class CorruptInstallerException : GogInstallerException
{
    /// <summary>Creates the exception with a message describing the failed check.</summary>
    public CorruptInstallerException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public CorruptInstallerException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The installer's compiled Pascal script could not be decoded.</summary>
public sealed class IfpsException : GogInstallerException
{
    /// <summary>Creates the exception with a message describing the failed check.</summary>
    public IfpsException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public IfpsException(string message, Exception inner) : base(message, inner) { }
}
