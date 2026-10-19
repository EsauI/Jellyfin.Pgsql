using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Runs Jellyfin on PostgreSQL. Selected with <c>PLUGIN_PROVIDER</c> in <c>database.xml</c>.
/// </summary>
public sealed partial class PostgreSqlDatabaseProvider : IJellyfinDatabaseProvider
{
    private const string ConnectionStringHint =
        "Set the connection string in database.xml under CustomProviderOptions/ConnectionString, "
        + "for example 'Host=postgres;Port=5432;Database=jellyfin;Username=jellyfin'. "
        + "The password may be supplied through the standard PGPASSWORD environment variable or a .pgpass file.";

    private readonly ILogger<PostgreSqlDatabaseProvider> _logger;
    private readonly RestoreCompletionInterceptor _restoreCompletion;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlDatabaseProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public PostgreSqlDatabaseProvider(ILogger<PostgreSqlDatabaseProvider> logger)
    {
        _logger = logger;
        _restoreCompletion = new RestoreCompletionInterceptor(logger);
    }

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(databaseConfiguration);

        var connectionString = databaseConfiguration.CustomProviderOptions?.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("No PostgreSQL connection string is configured. " + ConnectionStringHint);
        }

        NpgsqlConnectionStringBuilder connection;
        try
        {
            connection = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            // The inner exception is kept for diagnostics; the connection string itself is never echoed.
            throw new InvalidOperationException("The configured value is not a valid PostgreSQL connection string. " + ConnectionStringHint, ex);
        }

        options.UseNpgsql(
            connection.ConnectionString,
            npgsql => npgsql.MigrationsAssembly(typeof(PostgreSqlDatabaseProvider).Assembly.GetName().Name));
        options.AddInterceptors(_restoreCompletion);
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new CaseInsensitiveLikeExtension());

        LogConnecting(connection.Host, connection.Port, connection.Database);
    }

    /// <inheritdoc/>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // SQLite compares and sorts strings with its BINARY collation (ordinal, case-sensitive). The "C" collation
        // is the PostgreSQL equivalent and keeps ordering independent of the cluster's locale.
        modelBuilder.UseCollation("C");
    }

    /// <inheritdoc/>
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Applies to DateTime and DateTime? properties of every entity, including ones added later.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    /// <inheritdoc/>
    public async Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        var context = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            // VACUUM cannot run inside a transaction block; ExecuteSqlRaw does not open one. Its duration grows
            // with the database, so the command timeout is disabled and the cancellation token governs instead.
            context.Database.SetCommandTimeout(TimeSpan.Zero);
            await context.Database.ExecuteSqlRawAsync("VACUUM ANALYZE", cancellationToken).ConfigureAwait(false);
        }

        LogOptimised();
    }

    /// <inheritdoc/>
    public async Task RefreshStatistics(CancellationToken cancellationToken)
    {
        var context = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.Database.ExecuteSqlRawAsync("ANALYZE", cancellationToken).ConfigureAwait(false);
        }

        LogStatisticsRefreshed();
    }

    /// <inheritdoc/>
    public Task RunShutdownTask(CancellationToken cancellationToken)
    {
        NpgsqlConnection.ClearAllPools();
        LogPoolsCleared();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<string> MigrationBackupFast(CancellationToken cancellationToken)
        => throw new NotSupportedException("Backups are not supported by this provider version.");

    /// <inheritdoc/>
    public Task RestoreBackupFast(string key, CancellationToken cancellationToken)
        => throw new NotSupportedException("Backups are not supported by this provider version.");

    /// <inheritdoc/>
    public Task DeleteBackup(string key)
        => throw new NotSupportedException("Backups are not supported by this provider version.");

    /// <inheritdoc/>
    public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var modelTables = dbContext.Model.GetEntityTypes()
            .Select(entity => (Schema: entity.GetSchema(), Name: entity.GetTableName(), QualifiedName: entity.GetSchemaQualifiedTableName()))
            .Where(table => table.Name is not null)
            .Distinct()
            .ToList();

        var selected = new List<TableRef>();
        if (tableNames is null)
        {
            selected.AddRange(modelTables.Select(table => new TableRef(table.Schema, table.Name!)));
        }
        else
        {
            foreach (var name in tableNames.Distinct(StringComparer.Ordinal))
            {
                // The server passes schema-qualified names (BackupService uses GetSchemaQualifiedTableName).
                var match = modelTables.FirstOrDefault(table =>
                    string.Equals(table.QualifiedName, name, StringComparison.Ordinal)
                    || string.Equals(table.Name, name, StringComparison.Ordinal));
                if (match.Name is null)
                {
                    throw new ArgumentException($"'{name}' is not a table of the Jellyfin database model.", nameof(tableNames));
                }

                selected.Add(new TableRef(match.Schema, match.Name));
            }
        }

        if (selected.Count == 0)
        {
            return;
        }

        // Purge, restore save and follow-up form one transaction: a failure anywhere leaves the original data intact.
        var pending = await _restoreCompletion.BeginAsync(dbContext, CancellationToken.None).ConfigureAwait(false);
        try
        {
            // CASCADE also empties tables that reference the purged ones. SQLite keeps rows of tables the caller did
            // not list, so those are copied first and put back after the restore has been saved.
            var cascaded = CascadeResolver.FindCascaded(dbContext.Model, selected.Distinct().ToList());
            var snapshots = new List<TableSnapshot>();
            foreach (var table in cascaded)
            {
                snapshots.Add(await TableSnapshotter.CaptureAsync(dbContext, table, CancellationToken.None).ConfigureAwait(false));
            }

            var targets = string.Join(", ", selected.Select(table => table.Quoted));

            // Identifiers come from the EF model, are validated above and quoted, so the statement is not injectable.
#pragma warning disable EF1002, CA2100
            await dbContext.Database.ExecuteSqlRawAsync($"TRUNCATE TABLE {targets} RESTART IDENTITY CASCADE").ConfigureAwait(false);
#pragma warning restore EF1002, CA2100

            // Tables this purge empties on purpose are no longer held back, whatever an earlier purge kept of them.
            foreach (var table in selected)
            {
                pending.Snapshots.Remove(table);
            }

            // A table already emptied by an earlier purge of this context keeps the snapshot taken then.
            foreach (var snapshot in snapshots)
            {
                pending.Snapshots.TryAdd(snapshot.Table, snapshot);
            }

            LogPurged(selected.Count, snapshots.Count);
        }
        catch
        {
            await _restoreCompletion.AbortAsync(dbContext).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<JellyfinDbContext> CreateContextAsync(CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            throw new InvalidOperationException("The database context factory is not available; the provider has not been initialised by the server.");
        }

        return await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Using PostgreSQL database '{Database}' on {Host}:{Port}")]
    private partial void LogConnecting(string? host, int port, string? database);

    [LoggerMessage(Level = LogLevel.Information, Message = "PostgreSQL database optimised (VACUUM ANALYZE)")]
    private partial void LogOptimised();

    [LoggerMessage(Level = LogLevel.Information, Message = "PostgreSQL planner statistics refreshed (ANALYZE)")]
    private partial void LogStatisticsRefreshed();

    [LoggerMessage(Level = LogLevel.Information, Message = "PostgreSQL connection pools cleared")]
    private partial void LogPoolsCleared();

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} PostgreSQL table(s); kept the rows of {Kept} table(s) that the purge cascaded to")]
    private partial void LogPurged(int count, int kept);
}
