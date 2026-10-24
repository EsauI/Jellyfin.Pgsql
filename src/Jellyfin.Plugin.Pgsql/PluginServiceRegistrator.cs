using Jellyfin.Plugin.Pgsql.Setup;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Pgsql;

/// <summary>
/// Registers the services behind the configuration page.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IDatabaseConfigurationStore, DatabaseConfigurationStore>();
        serviceCollection.AddSingleton<IConnectionChecker, ConnectionChecker>();
        serviceCollection.AddSingleton<IPluginFolderInspector, PluginFolderInspector>();
        serviceCollection.AddSingleton<GuidedSetupService>();
    }
}
