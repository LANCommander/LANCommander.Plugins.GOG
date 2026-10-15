namespace LANCommander.GOG.Tests;

/// <summary>
/// Locates the real installers the integration tests run against.
/// </summary>
/// <remarks>
/// Installers are gigabytes each and cannot be committed, so these tests are skipped unless
/// <c>GOG_SAMPLE_DIR</c> points at a directory holding them.
/// </remarks>
public static class SampleInstallers
{
    /// <summary>Environment variable naming the directory that holds the sample installers.</summary>
    public const string DirectoryVariable = "GOG_SAMPLE_DIR";

    public static string? Directory => Environment.GetEnvironmentVariable(DirectoryVariable);

    public static bool Available => Directory is { Length: > 0 } path && System.IO.Directory.Exists(path);

    /// <summary>
    /// Resolves a sample by file name, or returns <see langword="null"/> when it is absent.
    /// </summary>
    public static string? Find(string fileName)
    {
        if (!Available)
            return null;

        var path = Path.Combine(Directory!, fileName);

        return File.Exists(path) ? path : null;
    }

    public const string CloseCombat2 = "setup_close_combat_2_2.0b_(17798).exe";
    public const string Il2Sturmovik = "setup_il-2_sturmovik_1946_4.13.4m_(14395).exe";
    public const string Rayman2 = "setup_rayman_2_gog-1_(17797).exe";
    public const string WheelOfTime = "setup_the_wheel_of_time_333b_(54846).exe";
    public const string Soldiers = "setup_soldiers_-_heroes_of_world_war_ii_1.28.3e_(18993).exe";
}

/// <summary>
/// Marks a test that needs a real installer, skipping it when none is configured.
/// </summary>
public sealed class InstallerFactAttribute : FactAttribute
{
    public InstallerFactAttribute(string fileName)
        => Skip = SkipReason(fileName);

    internal static string? SkipReason(string fileName) =>
        !SampleInstallers.Available
            ? $"Set {SampleInstallers.DirectoryVariable} to a directory of GOG installers to run this test."
            : SampleInstallers.Find(fileName) is null
                ? $"Sample installer \"{fileName}\" was not found in {SampleInstallers.Directory}."
                : null;
}

/// <summary>
/// Marks a theory whose cases each name an installer, skipping the ones that are absent.
/// </summary>
/// <remarks>
/// Skipping per case rather than for the whole theory means a partial sample directory still
/// exercises whatever it does hold.
/// </remarks>
public sealed class InstallerTheoryAttribute : TheoryAttribute
{
    public InstallerTheoryAttribute()
    {
        if (!SampleInstallers.Available)
            Skip = $"Set {SampleInstallers.DirectoryVariable} to a directory of GOG installers to run this test.";
    }
}
