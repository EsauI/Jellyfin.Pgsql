using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Creates a <see cref="JellyfinDbContext"/> for the <c>dotnet ef</c> tooling. The context is built through
/// <see cref="PostgreSqlDatabaseProvider"/>, so the model conventions and the migrations assembly match runtime.
/// </summary>
internal sealed class PostgreSqlDesignTimeDbContextFactory : IDesignTimeDbContextFactory<JellyfinDbContext>
{
    // Design time never opens a connection; the value only has to be a valid connection string.
    private const string PlaceholderConnectionString = "Host=localhost;Database=jellyfin-design-time;Username=jellyfin";

    /// <inheritdoc/>
    public JellyfinDbContext CreateDbContext(string[] args)
    {
        var provider = new PostgreSqlDatabaseProvider(NullLogger<PostgreSqlDatabaseProvider>.Instance);
        var options = new DbContextOptionsBuilder<JellyfinDbContext>();
        provider.Initialise(
            options,
            new DatabaseConfigurationOptions
            {
                DatabaseType = "PLUGIN_PROVIDER",
                CustomProviderOptions = new CustomDatabaseOptions
                {
                    PluginName = Plugin.PluginName,
                    PluginAssembly = typeof(PostgreSqlDesignTimeDbContextFactory).Assembly.GetName().Name!,
                    ConnectionString = PlaceholderConnectionString,
                },
            });

        return new JellyfinDbContext(
            options.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
