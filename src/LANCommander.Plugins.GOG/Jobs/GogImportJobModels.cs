using System.Text.Json.Serialization;

namespace LANCommander.Plugins.GOG.Jobs;

/// <summary>Where an import has got to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GogImportStage>))]
public enum GogImportStage
{
    Queued,
    Resolving,
    Downloading,
    Extracting,
    Packaging,
    Importing,
    Completed,
    Failed,
    Cancelled,
}

public static class GogImportStageExtensions
{
    /// <summary>True once an import has stopped, for whatever reason.</summary>
    public static bool IsFinished(this GogImportStage stage)
        => stage is GogImportStage.Completed or GogImportStage.Failed or GogImportStage.Cancelled;

    /// <summary>True while an import is waiting or running.</summary>
    public static bool IsActive(this GogImportStage stage) => !stage.IsFinished();
}

/// <summary>What an administrator asked to import.</summary>
/// <param name="AccountId">The linked account that owns the game. Empty for an uploaded installer.</param>
/// <param name="ProductId">GOG's id for the game. Empty for an uploaded installer until it is read.</param>
/// <param name="Title">The game's title in the GOG library, or the uploaded installer's name.</param>
/// <param name="Language">Preferred installer language, as GOG codes it (<c>en</c>, <c>de</c>, ...).</param>
/// <param name="StorageLocationId">Archive storage location to import into, or the default.</param>
/// <param name="UploadId">The uploaded installer to import, instead of downloading one from GOG.</param>
public sealed record GogImportRequest(
    Guid AccountId,
    string ProductId,
    string Title,
    string Language,
    Guid? StorageLocationId,
    Guid? UploadId = null)
{
    /// <summary>Imports an installer an administrator uploaded.</summary>
    public static GogImportRequest ForUpload(Guid uploadId, string title, Guid? storageLocationId)
        => new(Guid.Empty, string.Empty, title, string.Empty, storageLocationId, uploadId);
}

/// <summary>An import as it stands at one moment.</summary>
public sealed record GogImportJobSnapshot
{
    public Guid Id { get; init; }
    public Guid AccountId { get; init; }
    public string ProductId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Language { get; init; } = "en";
    public Guid? StorageLocationId { get; init; }

    /// <summary>The uploaded installer being imported, when it was not downloaded from GOG.</summary>
    public Guid? UploadId { get; init; }

    public GogImportStage Stage { get; init; }

    /// <summary>Progress through the current stage between 0 and 1, or null when it cannot be told.</summary>
    public double? Progress { get; init; }

    public long BytesCompleted { get; init; }
    public long BytesTotal { get; init; }

    /// <summary>Download speed, while downloading.</summary>
    public double? BytesPerSecond { get; init; }

    /// <summary>What the current stage is doing, for display.</summary>
    public string? Message { get; init; }

    public List<string> Warnings { get; init; } = [];
    public string? Error { get; init; }

    /// <summary>The installer chosen, once resolved.</summary>
    public string? InstallerName { get; init; }
    public string? InstallerVersion { get; init; }

    /// <summary>The game imported, once complete.</summary>
    public Guid? GameId { get; init; }

    public DateTimeOffset CreatedOn { get; init; }
    public DateTimeOffset? StartedOn { get; init; }
    public DateTimeOffset? CompletedOn { get; init; }

    /// <summary>
    /// True when this and <paramref name="other"/> import the same thing: the same upload, or the
    /// same game from GOG.
    /// </summary>
    public bool Duplicates(GogImportJobSnapshot other)
        => UploadId is { } upload
            ? upload == other.UploadId
            : other.UploadId is null && ProductId == other.ProductId;
}

/// <summary>What a finished import produced.</summary>
public sealed record GogImportOutcome(Guid GameId, string? Version, IReadOnlyList<string> Warnings);

/// <summary>Raised when an import cannot go ahead, with a message fit to show the administrator.</summary>
public sealed class GogImportException(string message, Exception? inner = null) : Exception(message, inner);
