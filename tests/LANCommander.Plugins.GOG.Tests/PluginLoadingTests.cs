using System.Reflection;
using System.Runtime.Loader;
using LANCommander.Plugins.GOG.Api;
using LANCommander.Plugins.GOG.Jobs;
using LANCommander.SDK.Enums;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.Plugins.GOG.Tests;

/// <summary>
/// Loads the built drop-in folder the way the server does, to prove it carries what it needs and
/// nothing that would load a second copy of a host type.
/// </summary>
public class PluginLoadingTests : IDisposable
{
    private static readonly string[] PrivateAssemblies = ["LANCommander.Plugins.GOG", "LANCommander.GOG", "LANCommander.GOG.Packaging", "SharpCompress"];

    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void TheDropInFolderHoldsOnlyThePluginsOwnAssemblies()
    {
        var assemblies = Directory.GetFiles(PluginOutputDirectory(), "*.dll").Select(Path.GetFileNameWithoutExtension).Order();

        Assert.Equal(PrivateAssemblies.Order(), assemblies);
        Assert.Empty(Directory.GetDirectories(PluginOutputDirectory()));
    }

    [Fact]
    public void TheServerLoadsThePluginAndItSharesTheHostsTypes()
    {
        var services = new ServiceCollection();
        var loader = new PluginLoaderService();

        loader.DiscoverAndConfigure(services, PluginHost.Server, Deploy(), "2.2.0", NullLogger.Instance);

        var manifest = Assert.Single(loader.LoadedPlugins);

        Assert.Equal(GogPlugin.PluginId, manifest.Id);

        // The contributions are the host's interface types, so the server can find them.
        var navigation = Assert.IsAssignableFrom<IServerNavigationExtension>(
            services.Single(service => service.ServiceType == typeof(IServerNavigationExtension)).ImplementationType is { } type
                ? Activator.CreateInstance(type, nonPublic: true)
                : null);

        Assert.Equal(GogRoutes.Library, navigation.Href);

        var routes = (IServerRouteAssemblyExtension)services
            .Single(service => service.ServiceType == typeof(IServerRouteAssemblyExtension)).ImplementationInstance!;

        var pluginContext = AssemblyLoadContext.GetLoadContext(routes.Assembly);

        Assert.IsType<PluginLoadContext>(pluginContext);

        // Every assembly the plugin references resolves to the host's copy, except its own.
        foreach (var reference in AllReferences(routes.Assembly, pluginContext!))
        {
            var loaded = pluginContext!.LoadFromAssemblyName(reference);
            var expected = PrivateAssemblies.Contains(reference.Name) ? pluginContext : AssemblyLoadContext.Default;

            Assert.True(
                AssemblyLoadContext.GetLoadContext(loaded) == expected,
                $"{reference.Name} loaded into {AssemblyLoadContext.GetLoadContext(loaded)?.Name}");
        }
    }

    [Theory]
    [InlineData("2.1.16", false)]
    [InlineData("2.2.0-alpha", true)]
    [InlineData("2.2.0", true)]
    public void TheServerSkipsThePluginWhenItPredatesTheHostServices(string hostVersion, bool loads)
    {
        var loader = new PluginLoaderService();

        loader.DiscoverAndConfigure(new ServiceCollection(), PluginHost.Server, Deploy(), hostVersion, NullLogger.Instance);

        Assert.Equal(loads, loader.LoadedPlugins.Count == 1);
    }

    [Fact]
    public void EveryServiceThePluginRegistersCanBeBuilt()
    {
        var services = new ServiceCollection();

        services.AddLogging();

        GogPlugin.AddGogServices(services, _ => _directory.PluginPaths);

        services.AddScoped<IPackageImporter, NoImporter>();
        services.AddScoped<IRedistributableProvider>(_ => new FixedRedistributables([]));
        services.AddHostedService<GogImportWorker>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IGogImportExecutor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GogLibraryService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GogAccountLinker>());
    }

    private string Deploy()
    {
        var root = Path.Combine(_directory.Path, "Plugins");
        var folder = Path.Combine(root, "LANCommander.Plugins.GOG");

        Directory.CreateDirectory(folder);

        foreach (var file in Directory.GetFiles(PluginOutputDirectory()))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));

        return root;
    }

    private static string PluginOutputDirectory()
        => typeof(PluginLoadingTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PluginOutputDirectory").Value!;

    /// <summary>The LANCommander and SharpCompress assemblies reachable from the plugin.</summary>
    private static IEnumerable<AssemblyName> AllReferences(Assembly root, AssemblyLoadContext context)
    {
        var seen = new HashSet<string>();
        var pending = new Queue<Assembly>([root]);

        while (pending.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is not { } name
                    || !(name.StartsWith("LANCommander.", StringComparison.Ordinal) || name == "SharpCompress")
                    || !seen.Add(name))
                    continue;

                yield return reference;

                var loaded = context.LoadFromAssemblyName(reference);

                if (PrivateAssemblies.Contains(name))
                    pending.Enqueue(loaded);
            }
        }
    }

    private sealed class NoImporter : IPackageImporter
    {
        public Task<PackageImportResult> ImportFileAsync(string packagePath, Guid? storageLocationId = null, ManifestType? manifestType = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PackageImportResult> ImportStreamAsync(Stream package, Guid? storageLocationId = null, ManifestType? manifestType = null, IProgress<long>? copyProgress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
