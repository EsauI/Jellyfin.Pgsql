using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Backs up and restores the Jellyfin database inside the PostgreSQL server with <c>CREATE DATABASE … TEMPLATE</c>,
/// so no PostgreSQL client tool and no file is involved.
/// </summary>
/// <remarks>
/// <para>
/// A backup is a database named <c>&lt;db&gt;_backup_&lt;key&gt;</c> cloned from the live one. A restore never touches the live
/// database in place: it clones the backup into <c>&lt;db&gt;_restore_&lt;stamp&gt;</c> (the backup stays intact) and swaps the
/// names, keeping the previous database as <c>&lt;db&gt;_failed_&lt;stamp&gt;</c>. The only database ever dropped is the backup a
/// delete names.
/// </para>
/// <para>
/// The PostgreSQL role needs the <c>CREATEDB</c> privilege and must own the source database (or be a superuser), because
/// PostgreSQL only lets the owner use a database as a template. Cloning is refused while another session is connected to the
/// source, so a backup waits briefly and then fails with a clear message; it never ends other sessions. The swap of a restore
/// does end the sessions on the live database, which is the point of a restore.
/// </para>
/// </remarks>
internal sealed partial class PostgreSqlBackupService
{
    /// <summary>
    /// The key returned when there was nothing to back up (no database yet, or a database without tables).
    /// </summary>
    internal const string NoBackupKey = "empty";

    private const string MaintenanceDatabase = "postgres";
    private const string PostgresInUse = "55006";
    private const string PostgresInvalidCatalogName = "3D000";

    private static readonly Regex KeyPattern = new(@"\A[0-9]{14}(_[0-9]+)?\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ILogger _logger;
    private readonly int _cloneAttempts;
    private readonly TimeSpan _cloneRetryDelay;
    private readonly int _renameAttempts;
    private readonly TimeSpan _renameRetryDelay;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlBackupService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="cloneAttempts">How many times a clone is tried while other sessions are connected to its source.</param>
    /// <param name="cloneRetryDelay">The pause between clone attempts.</param>
    /// <param name="renameAttempts">How many times a database rename is tried while other sessions hold it.</param>
    /// <param name="renameRetryDelay">The pause between rename attempts.</param>
    public PostgreSqlBackupService(
        ILogger logger,
        int cloneAttempts = 5,
        TimeSpan? cloneRetryDelay = null,
        int renameAttempts = 30,
        TimeSpan? renameRetryDelay = null)
    {
        _logger = logger;
        _cloneAttempts = cloneAttempts;
        _cloneRetryDelay = cloneRetryDelay ?? TimeSpan.FromSeconds(1);
        _renameAttempts = renameAttempts;
        _renameRetryDelay = renameRetryDelay ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Clones the database to a backup database.
    /// </summary>
    /// <param name="connection">The configured connection.</param>
    /// <param name="cancellationToken">Cancels the backup.</param>
    /// <returns>The backup key, or <see cref="NoBackupKey"/> when the database does not exist yet or has no tables.</returns>
    /// <exception cref="InvalidOperationException">Another client is connected to the database, or the role lacks the privileges.</exception>
    public async Task<string> BackupAsync(NpgsqlConnectionStringBuilder connection, CancellationToken cancellationToken)
    {
        var database = GetDatabase(connection);
        var probe = await ProbeAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!probe.Exists || !probe.HasTables)
        {
            // Jellyfin takes a backup before the first migration, including on a fresh install where the schema
            // does not exist (and sometimes the database neither). There is nothing to lose, so this must not fail.
            LogNothingToBackUp(database, probe.Exists);
            return NoBackupKey;
        }

        var maintenance = GetMaintenanceConnection(connection);
        var key = await PickUniqueKeyAsync(maintenance, database, cancellationToken).ConfigureAwait(false);
        var backupDatabase = DatabaseNames.Backup(database, key);

        LogBackingUp(database, backupDatabase);
        await CloneAsync(connection, maintenance, database, backupDatabase, cancellationToken).ConfigureAwait(false);
        return key;
    }

    /// <summary>
    /// Restores a backup by cloning it into a new database and swapping that in.
    /// </summary>
    /// <param name="connection">The configured connection.</param>
    /// <param name="key">A key returned by <see cref="BackupAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the restore before the swap starts.</param>
    /// <returns>A task that completes when the restored database serves the configured name.</returns>
    /// <exception cref="ArgumentException">The key was not created by this provider.</exception>
    /// <exception cref="InvalidOperationException">The backup does not exist, or the role lacks the privileges.</exception>
    public async Task RestoreAsync(NpgsqlConnectionStringBuilder connection, string key, CancellationToken cancellationToken)
    {
        if (string.Equals(key, NoBackupKey, StringComparison.Ordinal))
        {
            LogNothingToRestore();
            return;
        }

        RequireKey(key);
        var database = GetDatabase(connection);
        var backupDatabase = DatabaseNames.Backup(database, key);
        var maintenance = GetMaintenanceConnection(connection);

        if (!await DatabaseExistsAsync(maintenance, backupDatabase, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"PostgreSQL backup {key} does not exist (there is no database '{backupDatabase}'), nothing was restored.");
        }

        var stamp = await PickUniqueStampAsync(maintenance, database, cancellationToken).ConfigureAwait(false);
        var restoreDatabase = DatabaseNames.Restore(database, stamp);
        var failedDatabase = DatabaseNames.Failed(database, stamp);

        LogRestoring(backupDatabase, restoreDatabase, database);
        await CloneAsync(connection, maintenance, backupDatabase, restoreDatabase, cancellationToken).ConfigureAwait(false);

        // No cancellation from here: stopping between the two renames would leave no database under the configured name.
        await SwapAsync(connection, maintenance, database, restoreDatabase, failedDatabase).ConfigureAwait(false);
        LogRestored(database, backupDatabase, failedDatabase);
    }

    /// <summary>
    /// Drops the backup database of a key. Unknown keys, missing backups and <see cref="NoBackupKey"/> are ignored.
    /// </summary>
    /// <param name="connection">The configured connection.</param>
    /// <param name="key">A key returned by <see cref="BackupAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>A task that completes when the backup database is gone.</returns>
    public async Task DeleteAsync(NpgsqlConnectionStringBuilder connection, string key, CancellationToken cancellationToken)
    {
        if (string.Equals(key, NoBackupKey, StringComparison.Ordinal))
        {
            return;
        }

        if (!KeyPattern.IsMatch(key))
        {
            LogUnknownKey(key);
            return;
        }

        var backupDatabase = DatabaseNames.Backup(GetDatabase(connection), key);
        var maintenance = GetMaintenanceConnection(connection);
        if (!await DatabaseExistsAsync(maintenance, backupDatabase, cancellationToken).ConfigureAwait(false))
        {
            LogMissingBackup(backupDatabase);
            return;
        }

        var session = new NpgsqlConnection(maintenance.ConnectionString);
        await using (session.ConfigureAwait(false))
        {
            await session.OpenAsync(cancellationToken).ConfigureAwait(false);

            // The one database this ever drops is the backup the key names, computed above and never taken from input.
            await ExecuteAsync(session, $"DROP DATABASE {DatabaseNames.Quote(backupDatabase)}", cancellationToken).ConfigureAwait(false);
        }

        LogBackupDeleted(backupDatabase);
    }

    private static string GetDatabase(NpgsqlConnectionStringBuilder connection)
        => string.IsNullOrWhiteSpace(connection.Database)
            ? throw new InvalidOperationException("The PostgreSQL connection string does not name a database.")
            : connection.Database;

    private static NpgsqlConnectionStringBuilder GetMaintenanceConnection(NpgsqlConnectionStringBuilder connection)
        => new(connection.ConnectionString)
        {
            Database = MaintenanceDatabase,
            Pooling = false,
        };

    private static void RequireKey(string key)
    {
        if (!KeyPattern.IsMatch(key))
        {
            throw new ArgumentException($"'{key}' is not a PostgreSQL backup key created by this provider, nothing was restored.", nameof(key));
        }
    }

    // Idle pooled sessions of the application hold the database like any client. Npgsql keeps them open, so drop them.
    private static void ClearApplicationPools(NpgsqlConnectionStringBuilder connection)
    {
        var session = new NpgsqlConnection(connection.ConnectionString);
        try
        {
            NpgsqlConnection.ClearPool(session);
        }
        finally
        {
            session.Dispose();
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "DDL takes no parameters; every identifier passes DatabaseNames.Quote")]
    private static async Task ExecuteAsync(NpgsqlConnection session, string sql, CancellationToken cancellationToken)
    {
        var command = new NpgsqlCommand(sql, session) { CommandTimeout = 0 };
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> DatabaseExistsAsync(NpgsqlConnectionStringBuilder maintenance, string database, CancellationToken cancellationToken)
    {
        var session = new NpgsqlConnection(maintenance.ConnectionString);
        await using (session.ConfigureAwait(false))
        {
            await session.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @name)", session);
            await using (command.ConfigureAwait(false))
            {
                command.Parameters.AddWithValue("name", database);
                return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }
        }
    }

    private static bool IsTransient(NpgsqlException exception)
        => exception is not PostgresException postgres || postgres.SqlState == PostgresInUse;

    private static async Task<ServerProbe> ProbeAsync(NpgsqlConnectionStringBuilder connection, CancellationToken cancellationToken)
    {
        // Unpooled, so a one-off probe leaves no connection behind.
        var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString) { Pooling = false };
        try
        {
            var session = new NpgsqlConnection(builder.ConnectionString);
            await using (session.ConfigureAwait(false))
            {
                await session.OpenAsync(cancellationToken).ConfigureAwait(false);
                var command = new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace "
                    + "WHERE c.relkind IN ('r', 'p') AND n.nspname NOT IN ('pg_catalog', 'information_schema'))",
                    session);
                await using (command.ConfigureAwait(false))
                {
                    var hasTables = (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                    return new ServerProbe(true, hasTables);
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresInvalidCatalogName)
        {
            return new ServerProbe(false, false);
        }
    }

    private static string NewStamp() => DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private static async Task<string> PickUniqueKeyAsync(NpgsqlConnectionStringBuilder maintenance, string database, CancellationToken cancellationToken)
    {
        var key = NewStamp();
        for (var attempt = 1; await DatabaseExistsAsync(maintenance, DatabaseNames.Backup(database, key), cancellationToken).ConfigureAwait(false); attempt++)
        {
            key = string.Create(CultureInfo.InvariantCulture, $"{NewStamp()}_{attempt}");
        }

        return key;
    }

    private static async Task<string> PickUniqueStampAsync(NpgsqlConnectionStringBuilder maintenance, string database, CancellationToken cancellationToken)
    {
        var stamp = NewStamp();
        for (var attempt = 1;
            await DatabaseExistsAsync(maintenance, DatabaseNames.Restore(database, stamp), cancellationToken).ConfigureAwait(false)
            || await DatabaseExistsAsync(maintenance, DatabaseNames.Failed(database, stamp), cancellationToken).ConfigureAwait(false);
            attempt++)
        {
            stamp = string.Create(CultureInfo.InvariantCulture, $"{NewStamp()}_{attempt}");
        }

        return stamp;
    }

    // CREATE DATABASE ... TEMPLATE is refused while any other session is connected to the template. The application's own
    // idle pooled sessions are dropped first and the clone retried briefly, because a session that just closed can take a
    // moment to leave the server. Sessions that stay are someone else's: they are never ended for a backup.
    private async Task CloneAsync(
        NpgsqlConnectionStringBuilder application,
        NpgsqlConnectionStringBuilder maintenance,
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            ClearApplicationPools(application);
            try
            {
                var session = new NpgsqlConnection(maintenance.ConnectionString);
                await using (session.ConfigureAwait(false))
                {
                    await session.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await ExecuteAsync(session, $"CREATE DATABASE {DatabaseNames.Quote(target)} TEMPLATE {DatabaseNames.Quote(source)}", cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresInUse && attempt < _cloneAttempts)
            {
                LogCloneRetry(source, attempt, _cloneAttempts);
                await Task.Delay(_cloneRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresInUse)
            {
                throw new InvalidOperationException(
                    $"Cannot copy PostgreSQL database '{source}' because another client is connected to it. "
                    + "Close tools such as pgAdmin that have it open and try again; other sessions are never ended for a backup.",
                    ex);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                throw new InvalidOperationException(
                    $"The PostgreSQL role used by Jellyfin may not copy database '{source}'. Backups and restores clone databases, "
                    + "which requires the CREATEDB privilege (ALTER ROLE <role> CREATEDB) and ownership of the source database, "
                    + "or a superuser role. Nothing was changed.",
                    ex);
            }
        }
    }

    private async Task SwapAsync(NpgsqlConnectionStringBuilder application, NpgsqlConnectionStringBuilder maintenance, string database, string restoreDatabase, string failedDatabase)
    {
        ClearApplicationPools(application);

        var movedAside = false;
        if (await DatabaseExistsAsync(maintenance, database, CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                await RenameAsync(maintenance, database, failedDatabase).ConfigureAwait(false);
                movedAside = true;
            }
            catch (NpgsqlException ex)
            {
                LogMoveAsideFailed(ex, database, restoreDatabase);
                throw;
            }
        }

        try
        {
            await RenameAsync(maintenance, restoreDatabase, database).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSwapFailed(ex, restoreDatabase, database, failedDatabase);
            if (movedAside)
            {
                try
                {
                    await RenameAsync(maintenance, failedDatabase, database).ConfigureAwait(false);
                }
                catch (NpgsqlException backEx)
                {
                    LogNoDatabase(backEx, database, failedDatabase, restoreDatabase);
                }
            }

            throw;
        }
    }

    // A database with sessions on it cannot be renamed. End them and retry while the server reports it in use or
    // the connection drops; any other error is final. Each attempt opens its own session so a server restart
    // between the two renames does not leave every retry on a dead connection.
    private async Task RenameAsync(NpgsqlConnectionStringBuilder maintenance, string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var session = new NpgsqlConnection(maintenance.ConnectionString);
                await using (session.ConfigureAwait(false))
                {
                    await session.OpenAsync(CancellationToken.None).ConfigureAwait(false);

                    var terminate = new NpgsqlCommand(
                        "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = @name AND pid <> pg_backend_pid()",
                        session);
                    await using (terminate.ConfigureAwait(false))
                    {
                        terminate.Parameters.AddWithValue("name", from);
                        await terminate.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    await ExecuteAsync(session, $"ALTER DATABASE {DatabaseNames.Quote(from)} RENAME TO {DatabaseNames.Quote(to)}", CancellationToken.None).ConfigureAwait(false);
                    LogRenamed(from, to, attempt);
                    return;
                }
            }
            catch (NpgsqlException ex) when (attempt < _renameAttempts && IsTransient(ex))
            {
                LogRenameRetry(ex, from, to, attempt, _renameAttempts);
                await Task.Delay(_renameRetryDelay, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Nothing to back up in PostgreSQL database '{Database}' (database exists: {Exists}); using the empty backup key")]
    private partial void LogNothingToBackUp(string database, bool exists);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The empty backup key was asked to restore; there was nothing to restore, so the database is left as it is. The next start resumes from the recorded migration history")]
    private partial void LogNothingToRestore();

    [LoggerMessage(Level = LogLevel.Information, Message = "Backing up PostgreSQL database '{Database}' into database '{BackupDatabase}'")]
    private partial void LogBackingUp(string database, string backupDatabase);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot copy database '{Source}' yet: another session is connected to it (attempt {Attempt} of {Attempts}), retrying")]
    private partial void LogCloneRetry(string source, int attempt, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring unknown PostgreSQL backup key '{Key}'")]
    private partial void LogUnknownKey(string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PostgreSQL backup database '{BackupDatabase}' does not exist; nothing to delete")]
    private partial void LogMissingBackup(string backupDatabase);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped PostgreSQL backup database '{BackupDatabase}'")]
    private partial void LogBackupDeleted(string backupDatabase);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restoring PostgreSQL backup database '{BackupDatabase}' through new database '{RestoreDatabase}'; '{Database}' stays untouched until the copy completes")]
    private partial void LogRestoring(string backupDatabase, string restoreDatabase, string database);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PostgreSQL restore completed: '{Database}' now holds backup '{BackupDatabase}', which is kept. The previous database is kept as '{FailedDatabase}'; nothing removes it automatically")]
    private partial void LogRestored(string database, string backupDatabase, string failedDatabase);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not move {Database} aside, so it was not changed. The restored copy is complete in database {RestoreDatabase}; nothing removes it automatically")]
    private partial void LogMoveAsideFailed(Exception exception, string database, string restoreDatabase);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Could not move restored database {RestoreDatabase} to {Database}; renaming {FailedDatabase} back so the server finds a database")]
    private partial void LogSwapFailed(Exception exception, string restoreDatabase, string database, string failedDatabase);

    [LoggerMessage(Level = LogLevel.Critical, Message = "No database has the configured name {Database}: renaming {FailedDatabase} back failed too. The restored backup is in {RestoreDatabase}. To serve it run, on the postgres database: ALTER DATABASE \"{RestoreDatabase}\" RENAME TO \"{Database}\"")]
    private partial void LogNoDatabase(Exception exception, string database, string failedDatabase, string restoreDatabase);

    [LoggerMessage(Level = LogLevel.Information, Message = "Renamed database {From} to {To} (attempt {Attempt})")]
    private partial void LogRenamed(string from, string to, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Renaming database {From} to {To} failed on attempt {Attempt} of {Attempts}, retrying")]
    private partial void LogRenameRetry(Exception exception, string from, string to, int attempt, int attempts);
}
