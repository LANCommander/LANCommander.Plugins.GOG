using LANCommander.Plugins.GOG.Jobs;
using LANCommander.Plugins.GOG.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.Plugins.GOG.Tests;

public class JobServiceTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private GogImportJobService Service() => new(_directory.PluginPaths, NullLogger<GogImportJobService>.Instance);

    private static GogImportRequest Request(string productId = "123") => new(Guid.NewGuid(), productId, "Test Game", "en", null);

    [Fact]
    public void AQueuedImportWaitsForTheWorker()
    {
        var jobs = Service();

        var job = jobs.Enqueue(Request());

        Assert.Equal(GogImportStage.Queued, job.Stage);
        Assert.True(jobs.Queue.TryRead(out var queued));
        Assert.Equal(job.Id, queued);
    }

    [Fact]
    public void TheSameGameCannotBeQueuedTwice()
    {
        var jobs = Service();

        jobs.Enqueue(Request());

        Assert.Throws<InvalidOperationException>(() => jobs.Enqueue(Request()));
    }

    [Fact]
    public void ACancelledImportIsNotStarted()
    {
        var jobs = Service();
        var job = jobs.Enqueue(Request());

        Assert.True(jobs.Cancel(job.Id));
        Assert.Equal(GogImportStage.Cancelled, jobs.Get(job.Id)!.Stage);
        Assert.Null(jobs.TryStart(job.Id, CancellationToken.None));
    }

    [Fact]
    public void AFailedImportCanBeRetried()
    {
        var jobs = Service();
        var job = jobs.Enqueue(Request());

        jobs.Queue.TryRead(out _);
        jobs.TryStart(job.Id, CancellationToken.None);
        jobs.Fail(job.Id, "It broke");

        Assert.True(jobs.Retry(job.Id));

        var retried = jobs.Get(job.Id)!;

        Assert.Equal(GogImportStage.Queued, retried.Stage);
        Assert.Null(retried.Error);
        Assert.True(jobs.Queue.TryRead(out _));
    }

    [Fact]
    public void AnImportARestartCutShortComesBackAsFailed()
    {
        var jobs = Service();
        var job = jobs.Enqueue(Request());

        jobs.TryStart(job.Id, CancellationToken.None);
        jobs.ReporterFor(job.Id).Stage(GogImportStage.Downloading);

        var restarted = Service().Get(job.Id)!;

        Assert.Equal(GogImportStage.Failed, restarted.Stage);
        Assert.NotNull(restarted.Error);
        Assert.NotNull(restarted.CompletedOn);
    }

    [Fact]
    public void ProgressIsReportedAsAFraction()
    {
        var jobs = Service();
        var job = jobs.Enqueue(Request());

        jobs.TryStart(job.Id, CancellationToken.None);

        var reporter = jobs.ReporterFor(job.Id);

        reporter.Stage(GogImportStage.Downloading);
        reporter.Progress(25, 100);

        Assert.Equal(0.25, jobs.Get(job.Id)!.Progress);

        reporter.Stage(GogImportStage.Extracting);

        Assert.Null(jobs.Get(job.Id)!.Progress);
    }

    [Fact]
    public void ClearingRemovesOnlyFinishedImports()
    {
        var jobs = Service();
        var finished = jobs.Enqueue(Request("1"));
        var waiting = jobs.Enqueue(Request("2"));

        jobs.Cancel(finished.Id);

        Assert.Equal(1, jobs.ClearFinished());
        Assert.Equal(waiting.Id, Assert.Single(jobs.Jobs).Id);
    }
}

public class WorkerTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task ASuccessfulImportRecordsTheGame()
    {
        var gameId = Guid.NewGuid();
        var (worker, jobs) = Build((_, reporter, _) =>
        {
            reporter.Warning("Check the install script");

            return Task.FromResult(new GogImportOutcome(gameId, "1.0", []));
        });

        var job = jobs.Enqueue(new GogImportRequest(Guid.NewGuid(), "123", "Test Game", "en", null));

        await worker.RunAsync(job.Id, CancellationToken.None);

        var finished = jobs.Get(job.Id)!;

        Assert.Equal(GogImportStage.Completed, finished.Stage);
        Assert.Equal(gameId, finished.GameId);
        Assert.Equal(["Check the install script"], finished.Warnings);
    }

    [Fact]
    public async Task AFailureIsRecordedAgainstTheImportRatherThanThrown()
    {
        var (worker, jobs) = Build((_, _, _) => throw new GogImportException("GOG offers no Windows installer for this game."));

        var job = jobs.Enqueue(new GogImportRequest(Guid.NewGuid(), "123", "Test Game", "en", null));

        await worker.RunAsync(job.Id, CancellationToken.None);

        var failed = jobs.Get(job.Id)!;

        Assert.Equal(GogImportStage.Failed, failed.Stage);
        Assert.Equal("GOG offers no Windows installer for this game.", failed.Error);
    }

    [Fact]
    public async Task CancellingARunningImportStopsIt()
    {
        var started = new TaskCompletionSource();
        var (worker, jobs) = Build(async (_, _, token) =>
        {
            started.SetResult();

            await Task.Delay(Timeout.Infinite, token);

            throw new InvalidOperationException("Unreachable");
        });

        var job = jobs.Enqueue(new GogImportRequest(Guid.NewGuid(), "123", "Test Game", "en", null));
        var running = worker.RunAsync(job.Id, CancellationToken.None);

        await started.Task;

        Assert.True(jobs.Cancel(job.Id));

        await running;

        Assert.Equal(GogImportStage.Cancelled, jobs.Get(job.Id)!.Stage);
    }

    [Fact]
    public async Task AnImportCutShortByShutdownCanBeRetried()
    {
        using var stopping = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var (worker, jobs) = Build(async (_, _, token) =>
        {
            started.SetResult();

            await Task.Delay(Timeout.Infinite, token);

            throw new InvalidOperationException("Unreachable");
        });

        var job = jobs.Enqueue(new GogImportRequest(Guid.NewGuid(), "123", "Test Game", "en", null));
        var running = worker.RunAsync(job.Id, stopping.Token);

        await started.Task;
        await stopping.CancelAsync();
        await running;

        Assert.Equal(GogImportStage.Failed, jobs.Get(job.Id)!.Stage);
        Assert.True(jobs.Retry(job.Id));
    }

    private (GogImportWorker Worker, GogImportJobService Jobs) Build(
        Func<GogImportJobSnapshot, IGogImportReporter, CancellationToken, Task<GogImportOutcome>> execute)
    {
        var jobs = new GogImportJobService(_directory.PluginPaths, NullLogger<GogImportJobService>.Instance);

        var provider = new ServiceCollection()
            .AddScoped<IGogImportExecutor>(_ => new DelegateExecutor(execute))
            .BuildServiceProvider();

        var worker = new GogImportWorker(
            jobs,
            new GogSettingsStore(_directory.PluginPaths),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GogImportWorker>.Instance);

        return (worker, jobs);
    }

    private sealed class DelegateExecutor(
        Func<GogImportJobSnapshot, IGogImportReporter, CancellationToken, Task<GogImportOutcome>> execute) : IGogImportExecutor
    {
        public Task<GogImportOutcome> ExecuteAsync(GogImportJobSnapshot job, IGogImportReporter reporter, CancellationToken cancellationToken)
            => execute(job, reporter, cancellationToken);
    }
}
