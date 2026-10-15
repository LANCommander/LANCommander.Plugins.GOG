using LANCommander.Plugins.GOG.Api;
using LANCommander.Plugins.GOG.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.GOG.Jobs;

/// <summary>
/// Runs queued GOG imports in the background.
/// </summary>
/// <remarks>
/// Nothing may escape <see cref="ExecuteAsync"/>: the host stops itself when a background service
/// faults, so a failed import is recorded against the import and the worker carries on.
/// </remarks>
public sealed class GogImportWorker(
    GogImportJobService jobs,
    GogSettingsStore settings,
    IServiceScopeFactory scopes,
    ILogger<GogImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int concurrency;

        try
        {
            concurrency = Math.Clamp(settings.Current.MaxConcurrentImports, 1, 8);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the GOG plugin settings; running one import at a time");

            concurrency = 1;
        }

        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => ConsumeAsync(stoppingToken)));
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var id in jobs.Queue.ReadAllAsync(stoppingToken))
                await RunAsync(id, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task RunAsync(Guid id, CancellationToken stoppingToken)
    {
        if (jobs.TryStart(id, stoppingToken) is not { } claimed)
            return;

        var (job, token) = claimed;

        logger.LogInformation("Importing {Title} ({ProductId}) from GOG", job.Title, job.ProductId);

        try
        {
            await using var scope = scopes.CreateAsyncScope();

            var executor = scope.ServiceProvider.GetRequiredService<IGogImportExecutor>();
            var outcome = await executor.ExecuteAsync(job, jobs.ReporterFor(id), token);

            jobs.Complete(id, outcome);

            logger.LogInformation("Imported {Title} from GOG as game {GameId}", job.Title, outcome.GameId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                jobs.Fail(id, "The server stopped before this import finished.");
            }
            else
            {
                jobs.MarkCancelled(id);

                logger.LogInformation("Import of {Title} from GOG was cancelled", job.Title);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Importing {Title} ({ProductId}) from GOG failed", job.Title, job.ProductId);

            jobs.Fail(id, Describe(exception));
        }
    }

    /// <summary>Says why an import failed, in terms an administrator can act on.</summary>
    internal static string Describe(Exception exception) => exception switch
    {
        GogImportException or GogAuthenticationException or GogDownloadVerificationException => exception.Message,
        GogApiException api => $"GOG could not be reached: {api.Message}",
        HttpRequestException http => $"GOG could not be reached: {http.Message}",
        IOException io => $"Could not write the game files: {io.Message}",
        UnauthorizedAccessException access => $"Could not write the game files: {access.Message}",
        _ => exception.Message,
    };
}
