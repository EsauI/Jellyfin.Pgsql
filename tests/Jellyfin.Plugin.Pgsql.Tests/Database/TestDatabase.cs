using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Pgsql.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

/// <summary>
/// A Jellyfin database on the test server together with a provider configured for it.
/// </summary>
public sealed class TestDatabase
{
    private readonly DbContextOptions<JellyfinDbContext> _options;
    private readonly DatabaseConfigurationOptions _configuration;

    public TestDatabase(string connectionString)
    {
        ConnectionString = connectionString;

        // Short retries keep the test that holds a foreign session on the database quick.
        Provider = new PostgreSqlDatabaseProvider(
            NullLogger<PostgreSqlDatabaseProvider>.Instance,
            new PostgreSqlBackupService(NullLogger.Instance, cloneAttempts: 3, cloneRetryDelay: TimeSpan.FromMilliseconds(100)));
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        _configuration = new DatabaseConfigurationOptions
        {
            DatabaseType = "PLUGIN_PROVIDER",
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = Plugin.PluginName,
                PluginAssembly = "Jellyfin.Plugin.Pgsql",
                ConnectionString = connectionString,
            },
        };
        Provider.Initialise(builder, _configuration);
        _options = builder.Options;
        Provider.DbContextFactory = new Factory(this);
    }

    public string ConnectionString { get; }

    public string DatabaseName => new NpgsqlConnectionStringBuilder(ConnectionString).Database!;

    public PostgreSqlDatabaseProvider Provider { get; }

    /// <summary>
    /// Creates a factory that pools contexts the way the server does (<c>AddPooledDbContextFactory</c>).
    /// </summary>
    /// <param name="poolSize">The pool size; 1 makes the next lease reuse the context that was returned.</param>
    /// <returns>The factory.</returns>
    public IDbContextFactory<JellyfinDbContext> CreatePooledFactory(int poolSize = 1)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<JellyfinDbContext>>(NullLogger<JellyfinDbContext>.Instance);
        services.AddSingleton<IJellyfinDatabaseProvider>(Provider);
        services.AddSingleton<IEntityFrameworkCoreLockingBehavior>(new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        services.AddPooledDbContextFactory<JellyfinDbContext>(options => Provider.Initialise(options, _configuration), poolSize);
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
    }

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
