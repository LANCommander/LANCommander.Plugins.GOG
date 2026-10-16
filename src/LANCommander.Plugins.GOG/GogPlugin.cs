using System.Net.Http.Headers;
using System.Reflection;
using LANCommander.Plugins.GOG.Api;
using LANCommander.Plugins.GOG.Jobs;
using LANCommander.Plugins.GOG.Storage;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: LANCommanderPlugin(
    typeof(LANCommander.Plugins.GOG.GogPlugin),
    Id = LANCommander.Plugins.GOG.GogPlugin.PluginId,
    // IPackageImporter and IStorageLocationProvider arrived in 2.2.0; -0 admits its prereleases
    MinHostVersion = "2.2.0-0",
    Hosts = PluginHost.Server)]

namespace LANCommander.Plugins.GOG;

/// <summary>
/// Links GOG accounts to the server and imports the games they own.
/// </summary>
public sealed class GogPlugin : IPlugin
{
    public const string PluginId = "lancommander.gog";

    public string Id => PluginId;
    public string Name => "GOG";
    public string Version => typeof(GogPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string Author => "LANCommander";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IServerRouteAssemblyExtension>(new GogRouteAssembly());
        services.AddSingleton<IServerNavigationExtension, GogNavigation>();

        // IPackageImporter and IStorageLocationProvider come from the server
        AddGogServices(services, _ => GogPluginPaths.ForHost());

        services.AddHostedService<GogImportWorker>();
    }

    public Task InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        // Reading the history now, rather than on first use, settles imports a restart cut short
        // before anyone looks at them.
        var jobs = context.Services.GetRequiredService<GogImportJobService>();

        context.Logger.LogInformation(
            "GOG plugin keeps its state in {Root}; {Count} import(s) in history",
            context.Services.GetRequiredService<GogPluginPaths>().Root,
            jobs.Jobs.Count);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers everything the plugin needs that does not come from the host, so the tests can
    /// build the same container around a scratch directory.
    /// </summary>
    internal static void AddGogServices(IServiceCollection services, Func<IServiceProvider, GogPluginPaths> paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<GogSettingsStore>();
        services.AddSingleton(provider => new GogAccountStore(provider.GetRequiredService<GogPluginPaths>()));
        services.AddSingleton<GogImportMapStore>();
        services.AddSingleton<GogImportJobService>(provider => new GogImportJobService(
            provider.GetRequiredService<GogPluginPaths>(),
            provider.GetRequiredService<ILogger<GogImportJobService>>()));
        services.AddSingleton<GogLibraryCache>(_ => new GogLibraryCache());

        var userAgent = new ProductInfoHeaderValue("LANCommander-GOG", typeof(GogPlugin).Assembly.GetName().Version?.ToString(3) ?? "0");

        services.AddHttpClient<GogAuthClient>(client => client.DefaultRequestHeaders.UserAgent.Add(userAgent));
        services.AddHttpClient<GogApiClient>(client => client.DefaultRequestHeaders.UserAgent.Add(userAgent));
        services.AddHttpClient<GogDownloader>(client => client.DefaultRequestHeaders.UserAgent.Add(userAgent));

        services.AddTransient<GogSessionManager>();
        services.AddTransient<GogLibraryService>();
        services.AddTransient<GogAccountLinker>();
        services.AddScoped<IGogImportExecutor, GogImportExecutor>();
    }

    private sealed class GogRouteAssembly : IServerRouteAssemblyExtension
    {
        public Assembly Assembly => typeof(GogPlugin).Assembly;
    }

    private sealed class GogNavigation : IServerNavigationExtension
    {
        public string Id => $"{PluginId}.library";
        public string Label => "GOG";
        public string Href => GogRoutes.Library;
        public string? Icon => "Storefront";
        public int Order => 100;
    }
}

/// <summary>The plugin's pages.</summary>
public static class GogRoutes
{
    public const string Library = "/Plugins/GOG";
    public const string Imports = "/Plugins/GOG/Imports";
    public const string Accounts = "/Plugins/GOG/Accounts";
    public const string Settings = "/Plugins/GOG/Settings";
}
