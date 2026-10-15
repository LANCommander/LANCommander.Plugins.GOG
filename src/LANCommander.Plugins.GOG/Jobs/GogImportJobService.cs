using System.Threading.Channels;
using LANCommander.Plugins.GOG.Storage;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.GOG.Jobs;

/// <summary>Receives what a running import reports about itself.</summary>
public interface IGogImportReporter
{
    /// <summary>Moves the import on to a new stage, resetting its progress.</summary>
    void Stage(GogImportStage stage, string? message = null);

    /// <summary>Reports progress through the current stage.</summary>
    void Progress(long completed, long total, string? message = null);

    /// <summary>Records the installer the import settled on.</summary>
    void Installer(string name, string? version);

    /// <summary>Records something the administrator should look at, without failing the import.</summary>
    void Warning(string warning);
}

/// <summary>
/// The queue of GOG imports and the state of each.
/// </summary>
/// <remarks>
/// <para>
/// Imports run in <see cref="GogImportWorker"/>; this holds what pages read and what the worker
/// updates. Every change raises <see cref="Changed"/>. Progress changes are throttled so a page
/// is not asked to render for every buffer a download writes.
/// </para>
/// <para>
/// The list is written to disk whenever an import changes stage, so a restart keeps the history.
/// An import that was running when the server stopped comes back as failed, ready to retry.
/// </para>
/// </remarks>
public sealed class GogImportJobService
{
    /// <summary>How many finished imports are kept.</summary>
    public const int HistoryLimit = 200;

    private static readonly TimeSpan NotifyInterval = TimeSpan.FromMilliseconds(250);

    private readonly GogPluginPaths _paths;
    private readonly ILogger<GogImportJobService> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Entry> _jobs = [];
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();

    private long _lastNotify;
    private int _notifyPending;
    private bool _loaded;

    public GogImportJobService(GogPluginPaths paths, ILogger<GogImportJobService> logger, TimeProvider? time = null)
    {
        _paths = paths;
        _logger = logger;
        _time = time ?? TimeProvider.System;

        Load();
    }

    /// <summary>Raised after any import changes. May be raised on any thread.</summary>
    public event Action? Changed;

    /// <summary>Imports waiting to run, in the order they were queued.</summary>
    internal ChannelReader<Guid> Queue => _queue.Reader;

    /// <summary>Every import, newest first.</summary>
    public IReadOnlyList<GogImportJobSnapshot> Jobs
    {
        get
        {
            lock (_lock)
                return _jobs.Values.Select(entry => entry.Snapshot).OrderByDescending(job => job.CreatedOn).ToList();
        }
    }

    public GogImportJobSnapshot? Get(Guid id)
    {
        lock (_lock)
            return _jobs.GetValueOrDefault(id)?.Snapshot;
    }

    /// <summary>
    /// Restores the import history saved by an earlier run. Imports it left unfinished are marked
    /// as failed.
    /// </summary>
    private void Load()
    {
        lock (_lock)
        {
            if (_loaded)
                return;

            _loaded = true;

            List<GogImportJobSnapshot>? saved;

            try
            {
                saved = JsonFile.Read(_paths.Jobs, GogJsonContext.Default.ListGogImportJobSnapshot);
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
            {
                _logger.LogWarning(exception, "Could not read the GOG import history; starting a new one");

                saved = null;
            }

            foreach (var job in saved ?? [])
            {
                var restored = job.Stage.IsActive()
                    ? job with
                    {
                        Stage = GogImportStage.Failed,
                        Error = "The server stopped before this import finished.",
                        Progress = null,
                        BytesPerSecond = null,
                        CompletedOn = job.CompletedOn ?? _time.GetUtcNow(),
                    }
                    : job;

                _jobs[restored.Id] = new Entry(restored);
            }
        }
    }

    /// <summary>Queues an import.</summary>
    /// <exception cref="InvalidOperationException">The same game is already queued or importing.</exception>
    public GogImportJobSnapshot Enqueue(GogImportRequest request)
    {
        GogImportJobSnapshot job;

        lock (_lock)
        {
            if (_jobs.Values.Any(entry => entry.Snapshot.ProductId == request.ProductId && entry.Snapshot.Stage.IsActive()))
                throw new InvalidOperationException($"{request.Title} is already queued for import.");

            job = new GogImportJobSnapshot
            {
                Id = Guid.NewGuid(),
                AccountId = request.AccountId,
                ProductId = request.ProductId,
                Title = request.Title,
                Language = request.Language,
                StorageLocationId = request.StorageLocationId,
                Stage = GogImportStage.Queued,
                CreatedOn = _time.GetUtcNow(),
            };

            _jobs[job.Id] = new Entry(job);

            TrimHistory();
            Save();
        }

        _queue.Writer.TryWrite(job.Id);

        Notify(important: true);

        return job;
    }

    /// <summary>Stops an import, whether it is waiting or running.</summary>
    public bool Cancel(Guid id)
    {
        CancellationTokenSource? running;

        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var entry) || entry.Snapshot.Stage.IsFinished())
                return false;

            running = entry.Cancellation;

            if (running is not null)
            {
                // The worker sees the cancellation and records the outcome.
                entry.Snapshot = entry.Snapshot with { Message = "Cancelling" };
            }
            else
            {
                entry.Snapshot = entry.Snapshot with
                {
                    Stage = GogImportStage.Cancelled,
                    CompletedOn = _time.GetUtcNow(),
                    Message = null,
                };

                Save();
            }
        }

        try
        {
            running?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Finished in the meantime.
        }

        Notify(important: true);

        return true;
    }

    /// <summary>Queues a failed or cancelled import again.</summary>
    public bool Retry(Guid id)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var entry)
                || entry.Snapshot.Stage is not (GogImportStage.Failed or GogImportStage.Cancelled))
                return false;

            if (_jobs.Values.Any(other => other != entry
                                          && other.Snapshot.ProductId == entry.Snapshot.ProductId
                                          && other.Snapshot.Stage.IsActive()))
                return false;

            entry.Snapshot = entry.Snapshot with
            {
                Stage = GogImportStage.Queued,
                Error = null,
                Message = null,
                Warnings = [],
                Progress = null,
                BytesCompleted = 0,
                BytesTotal = 0,
                BytesPerSecond = null,
                StartedOn = null,
                CompletedOn = null,
            };

            Save();
        }

        _queue.Writer.TryWrite(id);

        Notify(important: true);

        return true;
    }

    /// <summary>Forgets a finished import.</summary>
    public bool Remove(Guid id)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var entry) || entry.Snapshot.Stage.IsActive())
                return false;

            _jobs.Remove(id);

            Save();
        }

        Notify(important: true);

        return true;
    }

    /// <summary>Forgets every finished import.</summary>
    public int ClearFinished()
    {
        int removed;

        lock (_lock)
        {
            var finished = _jobs.Values.Where(entry => entry.Snapshot.Stage.IsFinished()).Select(entry => entry.Snapshot.Id).ToList();

            foreach (var id in finished)
                _jobs.Remove(id);

            removed = finished.Count;

            if (removed > 0)
                Save();
        }

        if (removed > 0)
            Notify(important: true);

        return removed;
    }

    /// <summary>
    /// Claims a queued import for running.
    /// </summary>
    /// <returns>
    /// The import and the token that cancels it, or <see langword="null"/> when it is no longer
    /// waiting - cancelled before its turn, or queued twice.
    /// </returns>
    internal (GogImportJobSnapshot Job, CancellationToken Token)? TryStart(Guid id, CancellationToken stopping)
    {
        CancellationTokenSource cancellation;
        GogImportJobSnapshot job;

        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var entry) || entry.Snapshot.Stage != GogImportStage.Queued)
                return null;

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);

            entry.Cancellation = cancellation;
            entry.Snapshot = job = entry.Snapshot with
            {
                Stage = GogImportStage.Resolving,
                StartedOn = _time.GetUtcNow(),
                Message = "Looking up installers",
            };

            Save();
        }

        Notify(important: true);

        return (job, cancellation.Token);
    }

    /// <summary>A reporter that writes into the given import.</summary>
    internal IGogImportReporter ReporterFor(Guid id) => new Reporter(this, id);

    internal void Complete(Guid id, GogImportOutcome outcome)
        => Finish(id, snapshot => snapshot with
        {
            Stage = GogImportStage.Completed,
            GameId = outcome.GameId,
            Progress = 1,
            Message = null,
            Warnings = [.. snapshot.Warnings, .. outcome.Warnings.Except(snapshot.Warnings)],
        });

    internal void Fail(Guid id, string error)
        => Finish(id, snapshot => snapshot with { Stage = GogImportStage.Failed, Error = error });

    internal void MarkCancelled(Guid id)
        => Finish(id, snapshot => snapshot with { Stage = GogImportStage.Cancelled, Message = null });

    private void Finish(Guid id, Func<GogImportJobSnapshot, GogImportJobSnapshot> update)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var entry))
                return;

            entry.Cancellation?.Dispose();
            entry.Cancellation = null;
            entry.Snapshot = update(entry.Snapshot) with
            {
                CompletedOn = _time.GetUtcNow(),
                BytesPerSecond = null,
            };

            Save();
        }

        Notify(important: true);
    }

    private void Update(Guid id, bool important, Func<Entry, GogImportJobSnapshot> update)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var entry) || entry.Snapshot.Stage.IsFinished())
                return;

            entry.Snapshot = update(entry);

            if (important)
                Save();
        }

        Notify(important);
    }

    private void TrimHistory()
    {
        var excess = _jobs.Values
            .Where(entry => entry.Snapshot.Stage.IsFinished())
            .OrderByDescending(entry => entry.Snapshot.CreatedOn)
            .Skip(HistoryLimit)
            .Select(entry => entry.Snapshot.Id)
            .ToList();

        foreach (var id in excess)
            _jobs.Remove(id);
    }

    /// <remarks>Called with the lock held.</remarks>
    private void Save()
    {
        try
        {
            JsonFile.Write(_paths.Jobs, _jobs.Values.Select(entry => entry.Snapshot).ToList(), GogJsonContext.Default.ListGogImportJobSnapshot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing the history is a nuisance; failing the import over it would be worse.
            _logger.LogWarning(exception, "Could not save the GOG import history");
        }
    }

    private void Notify(bool important)
    {
        var now = _time.GetTimestamp();

        if (important || _time.GetElapsedTime(Interlocked.Read(ref _lastNotify), now) >= NotifyInterval)
        {
            Interlocked.Exchange(ref _lastNotify, now);
            Raise();

            return;
        }

        if (Interlocked.Exchange(ref _notifyPending, 1) == 1)
            return;

        _ = Task.Delay(NotifyInterval, _time).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _notifyPending, 0);
            Interlocked.Exchange(ref _lastNotify, _time.GetTimestamp());
            Raise();
        }, TaskScheduler.Default);
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A GOG import listener failed");
        }
    }

    private sealed class Entry(GogImportJobSnapshot snapshot)
    {
        public GogImportJobSnapshot Snapshot { get; set; } = snapshot;

        public CancellationTokenSource? Cancellation { get; set; }

        /// <summary>The last download sample, for working out speed.</summary>
        public (long Timestamp, long Bytes)? Sample { get; set; }
    }

    private sealed class Reporter(GogImportJobService service, Guid id) : IGogImportReporter
    {
        public void Stage(GogImportStage stage, string? message = null)
            => service.Update(id, important: true, entry =>
            {
                entry.Sample = null;

                return entry.Snapshot with
                {
                    Stage = stage,
                    Message = message,
                    Progress = null,
                    BytesCompleted = 0,
                    BytesTotal = 0,
                    BytesPerSecond = null,
                };
            });

        public void Progress(long completed, long total, string? message = null)
            => service.Update(id, important: false, entry =>
            {
                var snapshot = entry.Snapshot;
                var now = service._time.GetTimestamp();
                var speed = snapshot.BytesPerSecond;

                if (snapshot.Stage == GogImportStage.Downloading)
                {
                    if (entry.Sample is { } previous)
                    {
                        var seconds = service._time.GetElapsedTime(previous.Timestamp, now).TotalSeconds;

                        if (seconds >= 1)
                        {
                            var current = Math.Max(0, completed - previous.Bytes) / seconds;

                            // Smoothed, so the figure does not jump about with every sample.
                            speed = speed is { } last ? last * 0.7 + current * 0.3 : current;
                            entry.Sample = (now, completed);
                        }
                    }
                    else
                    {
                        entry.Sample = (now, completed);
                    }
                }

                return snapshot with
                {
                    BytesCompleted = completed,
                    BytesTotal = total,
                    Progress = total > 0 ? Math.Clamp((double)completed / total, 0, 1) : null,
                    BytesPerSecond = speed,
                    Message = message ?? snapshot.Message,
                };
            });

        public void Installer(string name, string? version)
            => service.Update(id, important: true, entry => entry.Snapshot with
            {
                InstallerName = name,
                InstallerVersion = version,
            });

        public void Warning(string warning)
            => service.Update(id, important: true, entry => entry.Snapshot.Warnings.Contains(warning)
                ? entry.Snapshot
                : entry.Snapshot with { Warnings = [.. entry.Snapshot.Warnings, warning] });
    }
}
