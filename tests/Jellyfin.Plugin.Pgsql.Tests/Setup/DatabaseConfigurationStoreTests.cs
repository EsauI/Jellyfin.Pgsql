using System.IO;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Pgsql.Setup;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Setup;

public class DatabaseConfigurationStoreTests
{
    private readonly IServerConfigurationManager _manager = Substitute.For<IServerConfigurationManager>();
    private readonly IApplicationPaths _paths = Substitute.For<IApplicationPaths>();

    [Fact]
    public void SavePostgreSql_WritesTheExpectedDatabaseXmlContent()
    {
        _manager.GetConfiguration("database").Returns(new DatabaseConfigurationOptions
        {
            DatabaseType = "Jellyfin-SQLite",
            LockingBehavior = DatabaseLockingBehaviorTypes.Pessimistic,
        });

        new DatabaseConfigurationStore(_manager, _paths).SavePostgreSql("Host=db;Username=jf");

        _manager.Received(1).SaveConfiguration(
            "database",
            Arg.Is<object>(o => IsPostgreSql((DatabaseConfigurationOptions)o)));
    }

    [Fact]
    public void SaveSqlite_WritesSqliteAndKeepsLockingBehavior()
    {
        _manager.GetConfiguration("database").Returns(new DatabaseConfigurationOptions
        {
            DatabaseType = "PLUGIN_PROVIDER",
            LockingBehavior = DatabaseLockingBehaviorTypes.Optimistic,
            CustomProviderOptions = new CustomDatabaseOptions { PluginName = "PostgreSQL", PluginAssembly = "Jellyfin.Plugin.Pgsql.dll", ConnectionString = "Host=db" },
        });

        new DatabaseConfigurationStore(_manager, _paths).SaveSqlite();

        _manager.Received(1).SaveConfiguration(
            "database",
            Arg.Is<object>(o => ((DatabaseConfigurationOptions)o).DatabaseType == "Jellyfin-SQLite"
                && ((DatabaseConfigurationOptions)o).CustomProviderOptions == null
                && ((DatabaseConfigurationOptions)o).LockingBehavior == DatabaseLockingBehaviorTypes.Optimistic));
    }

    [Fact]
    public void SavePostgreSql_KeepsLockingBehavior()
    {
        _manager.GetConfiguration("database").Returns(new DatabaseConfigurationOptions
        {
            DatabaseType = "Jellyfin-SQLite",
            LockingBehavior = DatabaseLockingBehaviorTypes.Pessimistic,
        });

        new DatabaseConfigurationStore(_manager, _paths).SavePostgreSql("Host=db");

        _manager.Received(1).SaveConfiguration(
            "database",
            Arg.Is<object>(o => ((DatabaseConfigurationOptions)o).LockingBehavior == DatabaseLockingBehaviorTypes.Pessimistic));
    }

    [Fact]
    public void IsServerSetUp_ReflectsTheStartupWizard()
    {
        _manager.Configuration.Returns(new ServerConfiguration { IsStartupWizardCompleted = true });

        Assert.True(new DatabaseConfigurationStore(_manager, _paths).IsServerSetUp);
    }

    [Fact]
    public void SqliteDatabaseExists_LooksForJellyfinDbInTheDataPath()
    {
        var directory = Directory.CreateTempSubdirectory("jf-store-").FullName;
        _paths.DataPath.Returns(directory);
        var store = new DatabaseConfigurationStore(_manager, _paths);

        Assert.False(store.SqliteDatabaseExists);
        File.WriteAllText(Path.Combine(directory, "jellyfin.db"), string.Empty);
        Assert.True(store.SqliteDatabaseExists);
    }

    [Fact]
    public void SqliteLastModifiedUtc_ReportsTheFileTimestamp()
    {
        var directory = Directory.CreateTempSubdirectory("jf-store-").FullName;
        _paths.DataPath.Returns(directory);
        var store = new DatabaseConfigurationStore(_manager, _paths);
        Assert.Null(store.SqliteLastModifiedUtc);

        var file = Path.Combine(directory, "jellyfin.db");
        File.WriteAllText(file, string.Empty);
        File.SetLastWriteTimeUtc(file, new System.DateTime(2026, 10, 1, 12, 30, 0, System.DateTimeKind.Utc));

        Assert.Equal(new System.DateTime(2026, 10, 1, 12, 30, 0, System.DateTimeKind.Utc), store.SqliteLastModifiedUtc);
    }

    private static bool IsPostgreSql(DatabaseConfigurationOptions o)
        => o.DatabaseType == "PLUGIN_PROVIDER"
            && o.CustomProviderOptions is { PluginName: "PostgreSQL", PluginAssembly: "Jellyfin.Plugin.Pgsql.dll", ConnectionString: "Host=db;Username=jf" }
            && o.CustomProviderOptions.Options.Count == 0;
}
