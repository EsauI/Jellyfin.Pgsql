using System;
using System.IO;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Pgsql.Setup;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Setup;

public sealed class DatabaseConfigurationStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("jf-passfile-").FullName;

    private readonly IServerConfigurationManager _manager = Substitute.For<IServerConfigurationManager>();
    private readonly IApplicationPaths _paths = Substitute.For<IApplicationPaths>();

    private string PassfilePath => Path.Combine(_directory, "pgsql.pgpass");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

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

    [Fact]
    public void SavePostgreSql_NeverWritesThePasswordIntoDatabaseXml()
    {
        var (store, saved) = NewStore();

        store.SavePostgreSql("Host=db;Port=5433;Username=jf;Password=s3cret-value");

        var xml = new NpgsqlConnectionStringBuilder(saved().CustomProviderOptions!.ConnectionString);
        Assert.DoesNotContain("s3cret-value", saved().CustomProviderOptions!.ConnectionString, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(xml.Password));
        Assert.Equal(PassfilePath, xml.Passfile);
        Assert.Equal("db", xml.Host);
    }

    [Fact]
    public void SavePostgreSql_WritesThePasswordToAnOwnerOnlyPassfile()
    {
        var (store, _) = NewStore();

        store.SavePostgreSql("Host=db;Port=5433;Username=jf;Password=s3cret-value");

        Assert.Equal("db:5433:*:jf:s3cret-value", File.ReadAllText(PassfilePath).Trim());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PassfilePath));
        }
    }

    [Fact]
    public void SavePostgreSql_ReplacesAnExistingPassfileWithoutLeavingTemporaryFiles()
    {
        var (store, _) = NewStore();
        store.SavePostgreSql("Host=db;Username=jf;Password=first");

        store.SavePostgreSql("Host=other;Username=jf;Password=second");

        Assert.Equal("other:5432:*:jf:second", File.ReadAllText(PassfilePath).Trim());
        Assert.Equal([PassfilePath], Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("a:b")]
    [InlineData("back\\slash")]
    [InlineData("semi;colon")]
    [InlineData("it's \"quoted\"")]
    [InlineData("all:\\;'\" together")]
    [InlineData("#starts-with-hash")]
    public void ReadPassword_RoundTripsWhatSavePostgreSqlStored(string password)
    {
        var (store, saved) = NewStore();
        var connection = new NpgsqlConnectionStringBuilder { Host = "db", Username = "jf", Password = password };

        store.SavePostgreSql(connection.ConnectionString);
        _manager.GetConfiguration("database").Returns(saved());

        Assert.Equal(password, store.ReadPassword());
        Assert.DoesNotContain(password, saved().CustomProviderOptions!.ConnectionString, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadPassword_ReturnsAPasswordStillEmbeddedInTheConnectionString()
    {
        _manager.GetConfiguration("database").Returns(new DatabaseConfigurationOptions
        {
            DatabaseType = "PLUGIN_PROVIDER",
            CustomProviderOptions = new CustomDatabaseOptions { PluginName = "PostgreSQL", PluginAssembly = "Jellyfin.Plugin.Pgsql.dll", ConnectionString = "Host=db;Username=jf;Password=legacy" },
        });

        Assert.Equal("legacy", new DatabaseConfigurationStore(_manager, _paths).ReadPassword());
    }

    [Fact]
    public void ReadPassword_ReturnsNullWhenNothingIsStored()
    {
        _manager.GetConfiguration("database").Returns(new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-SQLite" });
        _paths.ConfigurationDirectoryPath.Returns(_directory);

        Assert.Null(new DatabaseConfigurationStore(_manager, _paths).ReadPassword());
    }

    [Fact]
    public void SavePostgreSql_WithoutAPassword_LeavesTheStoredPassfileAlone()
    {
        var (store, saved) = NewStore();
        store.SavePostgreSql("Host=db;Username=jf;Password=kept");

        store.SavePostgreSql("Host=db;Username=jf");

        Assert.Equal("db:5432:*:jf:kept", File.ReadAllText(PassfilePath).Trim());
        Assert.NotNull(saved());
    }

    [Fact]
    public void SaveSqlite_LeavesThePassfileInPlace()
    {
        var (store, _) = NewStore();
        store.SavePostgreSql("Host=db;Username=jf;Password=kept");

        store.SaveSqlite();

        Assert.True(File.Exists(PassfilePath));
    }

    [Fact]
    public void SavePostgreSql_PreservesTheExistingCustomProviderOptions()
    {
        var existing = new CustomDatabaseOptions { PluginName = "PostgreSQL", PluginAssembly = "Jellyfin.Plugin.Pgsql.dll", ConnectionString = "Host=old" };
        existing.Options.Add(new CustomDatabaseOption { Key = "tuning", Value = "on" });
        _manager.GetConfiguration("database").Returns(new DatabaseConfigurationOptions { DatabaseType = "PLUGIN_PROVIDER", CustomProviderOptions = existing });
        var (store, saved) = NewStore(configure: false);

        store.SavePostgreSql("Host=db;Username=jf");

        var option = Assert.Single(saved().CustomProviderOptions!.Options);
        Assert.Equal("tuning", option.Key);
        Assert.Equal("on", option.Value);
    }

    private static bool IsPostgreSql(DatabaseConfigurationOptions o)
        => o.DatabaseType == "PLUGIN_PROVIDER"
            && o.CustomProviderOptions is { PluginName: "PostgreSQL", PluginAssembly: "Jellyfin.Plugin.Pgsql.dll", ConnectionString: "Host=db;Username=jf" }
            && o.CustomProviderOptions.Options.Count == 0;

    private (DatabaseConfigurationStore Store, Func<DatabaseConfigurationOptions> Saved) NewStore(bool configure = true)
    {
        if (configure)
        {
            _manager.GetConfiguration("database").Returns(new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-SQLite" });
        }

        _paths.ConfigurationDirectoryPath.Returns(_directory);
        DatabaseConfigurationOptions? saved = null;
        _manager.When(m => m.SaveConfiguration("database", Arg.Any<object>())).Do(call => saved = (DatabaseConfigurationOptions)call.ArgAt<object>(1));
        return (new DatabaseConfigurationStore(_manager, _paths), () => saved!);
    }
}
