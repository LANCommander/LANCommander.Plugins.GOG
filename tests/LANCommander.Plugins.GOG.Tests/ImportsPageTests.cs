using Bunit;
using LANCommander.Plugins.GOG.Jobs;
using LANCommander.Plugins.GOG.Pages;
using LANCommander.Server.UI.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.Plugins.GOG.Tests;

public class ImportsPageTests : BunitContext
{
    private readonly TempDirectory _directory = new();
    private readonly GogImportJobService _jobs;

    public ImportsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddLANCommanderServerUI();

        _jobs = new GogImportJobService(_directory.PluginPaths, NullLogger<GogImportJobService>.Instance);

        Services.AddSingleton(_jobs);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
            _directory.Dispose();
    }

    [Fact]
    public void ThePageFollowsAnImportAsItRuns()
    {
        var page = Render<ImportsPage>();

        Assert.Contains("Nothing has been imported yet", page.Markup);

        var job = _jobs.Enqueue(new GogImportRequest(Guid.NewGuid(), "123", "Close Combat 2", "en", null));

        page.WaitForAssertion(() => Assert.Contains("Close Combat 2", page.Markup));
        page.WaitForAssertion(() => Assert.Contains("Queued", page.Markup));

        _jobs.TryStart(job.Id, CancellationToken.None);

        var reporter = _jobs.ReporterFor(job.Id);

        reporter.Stage(GogImportStage.Downloading, "Downloading installer");
        reporter.Progress(512L * 1024 * 1024, 1024L * 1024 * 1024);

        page.WaitForAssertion(() => Assert.Contains("512.0 MB of 1.0 GB", page.Markup));

        _jobs.Fail(job.Id, "GOG could not be reached.");

        page.WaitForAssertion(() =>
        {
            Assert.Contains("GOG could not be reached.", page.Markup);
            Assert.Contains("Retry", page.Markup);
        });
    }
}
