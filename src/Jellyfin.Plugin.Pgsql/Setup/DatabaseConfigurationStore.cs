using System;
using System.IO;
using Jellyfin.Database.Implementations.DbConfiguration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// <see cref="IDatabaseConfigurationStore"/> backed by the server configuration manager.
/// </summary>
/// <param name="configurationManager">The server configuration manager.</param>
/// <param name="applicationPaths">The server paths.</param>
public class DatabaseConfigurationStore(IServerConfigurationManager configurationManager, IApplicationPaths applicationPaths) : IDatabaseConfigurationStore
{
    private const string StoreKey = "database";

    /// <inheritdoc />
    public bool IsServerSetUp => configurationManager.Configuration.IsStartupWizardCompleted;

    /// <inheritdoc />
    public bool SqliteDatabaseExists => File.Exists(Path.Combine(applicationPaths.DataPath, "jellyfin.db"));

    /// <inheritdoc />
    public DateTime? SqliteLastModifiedUtc => SqliteDatabaseExists
        ? File.GetLastWriteTimeUtc(Path.Combine(applicationPaths.DataPath, "jellyfin.db"))
        : null;

    /// <inheritdoc />
    public DatabaseConfigurationOptions Read() => configurationManager.GetConfiguration<DatabaseConfigurationOptions>(StoreKey);

    /// <inheritdoc />
    public void SavePostgreSql(string connectionString)
    {
        configurationManager.SaveConfiguration(
            StoreKey,
            new DatabaseConfigurationOptions
            {
                DatabaseType = "PLUGIN_PROVIDER",
                LockingBehavior = Read().LockingBehavior,

                // The .dll is required: Jellyfin swaps the extension to load the assembly.
                CustomProviderOptions = new CustomDatabaseOptions
                {
                    PluginName = Plugin.PluginName,
                    PluginAssembly = "Jellyfin.Plugin.Pgsql.dll",
                    ConnectionString = connectionString,
                },
            });
    }

    /// <inheritdoc />
    public void SaveSqlite()
    {
        configurationManager.SaveConfiguration(
            StoreKey,
            new DatabaseConfigurationOptions
            {
                DatabaseType = "Jellyfin-SQLite",
                LockingBehavior = Read().LockingBehavior,
            });
    }
}
