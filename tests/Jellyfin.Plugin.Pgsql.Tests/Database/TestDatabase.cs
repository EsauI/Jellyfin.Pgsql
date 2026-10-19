using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Pgsql.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

/// <summary>
/// A Jellyfin database on the test server together with a provider configured for it.
/// </summary>
public sealed class TestDatabase
{
    private readonly DbContextOptions<JellyfinDbContext> _options;

    public TestDatabase(string connectionString)
    {
        Provider = new PostgreSqlDatabaseProvider(NullLogger<PostgreSqlDatabaseProvider>.Instance);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        Provider.Initialise(builder, new DatabaseConfigurationOptions
        {
            DatabaseType = "PLUGIN_PROVIDER",
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = Plugin.PluginName,
                PluginAssembly = "Jellyfin.Plugin.Pgsql",
                ConnectionString = connectionString,
            },
        });
        _options = builder.Options;
        Provider.DbContextFactory = new Factory(this);
    }

    public PostgreSqlDatabaseProvider Provider { get; }

    public JellyfinDbContext CreateContext() => new(
        _options,
        NullLogger<JellyfinDbContext>.Instance,
        Provider,
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    private sealed class Factory(TestDatabase database) : IDbContextFactory<JellyfinDbContext>
    {
        public JellyfinDbContext CreateDbContext() => database.CreateContext();
    }
}
