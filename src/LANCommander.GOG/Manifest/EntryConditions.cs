using LANCommander.GOG.Inno;

namespace LANCommander.GOG.Manifest;

/// <summary>
/// The guards Inno Setup attaches to most installable items, deciding whether the item
/// applies to a given installation.
/// </summary>
/// <remarks>
/// These are recorded verbatim. Evaluating <see cref="Check"/>, <see cref="BeforeInstall"/>
/// or <see cref="AfterInstall"/> would mean running the installer's compiled script, which
/// this library deliberately never does.
/// </remarks>
/// <param name="Components">Components this item belongs to, or empty for all.</param>
/// <param name="Tasks">Tasks that must be selected, or empty for none.</param>
/// <param name="Languages">Languages this item is limited to, or empty for all.</param>
/// <param name="Check">Name of a script function gating this item.</param>
/// <param name="BeforeInstall">Script procedure invoked before the item is installed.</param>
/// <param name="AfterInstall">Script procedure invoked after the item is installed.</param>
/// <param name="MinVersion">Lowest Windows version the item applies to.</param>
/// <param name="OnlyBelowVersion">Exclusive upper Windows version bound.</param>
public sealed record EntryConditions(
    string Components,
    string Tasks,
    string Languages,
    string Check,
    string BeforeInstall,
    string AfterInstall,
    WindowsVersionRequirement MinVersion,
    WindowsVersionRequirement OnlyBelowVersion)
{
    /// <summary>An unconditional item.</summary>
    public static EntryConditions None { get; } = new(
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
        default, default);

    /// <summary>
    /// True when the item is gated by script code, meaning its applicability cannot be
    /// determined by static analysis alone.
    /// </summary>
    public bool RequiresScriptEvaluation => Check.Length > 0;
}
