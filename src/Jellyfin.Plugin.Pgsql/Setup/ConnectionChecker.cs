using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// <see cref="IConnectionChecker"/> that talks to a real PostgreSQL server.
/// </summary>
public class ConnectionChecker : IConnectionChecker
{
    private const string HistoryTable = "__EFMigrationsHistory";
    private const string BaselineSuffix = "_InitialCreate";
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(20);

    /// <inheritdoc />
    public async Task<ConnectionAssessment> CheckAsync(string connectionString, CancellationToken cancellationToken)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Timeout = 5, CommandTimeout = 10 };
        }
        catch (ArgumentException)
        {
            return Failed("The connection settings are not valid.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OverallTimeout);
        try
        {
            var connection = new NpgsqlConnection(builder.ConnectionString);
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(timeout.Token).ConfigureAwait(false);
                return await InspectAsync(connection, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(Unreachable(builder));
        }
        catch (PostgresException ex)
        {
            return Failed(Describe(ex, builder));
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or System.IO.IOException or System.Net.Sockets.SocketException)
        {
            return Failed(Unreachable(builder));
        }
    }

    private static async Task<ConnectionAssessment> InspectAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var assessment = new ConnectionAssessment { Connected = true, ServerVersion = connection.PostgreSqlVersion.ToString() };

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            // A superuser passes both checks: it may create databases and clone any database.
            command.CommandText = """
                SELECT r.rolcreatedb OR r.rolsuper, r.rolsuper OR d.datdba = r.oid
                FROM pg_roles r, pg_database d
                WHERE r.rolname = current_user AND d.datname = current_database()
                """;
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    assessment.CanCreateDatabase = reader.GetBoolean(0);
                    assessment.OwnsDatabase = reader.GetBoolean(1);
                }
            }
        }

        assessment.TargetState = await ReadTargetStateAsync(connection, cancellationToken).ConfigureAwait(false);
        return assessment;
    }

    private static async Task<TargetState> ReadTargetStateAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "SELECT tablename FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema')";
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    tables.Add(reader.GetString(0));
                }
            }
        }

        if (tables.Count == 0)
        {
            return TargetState.Empty;
        }

        if (!tables.Contains(HistoryTable))
        {
            return TargetState.Other;
        }

        var history = connection.CreateCommand();
        await using (history.ConfigureAwait(false))
        {
            history.CommandText = $"SELECT \"MigrationId\" FROM \"{HistoryTable}\"";
            var reader = await history.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var rows = 0;
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows++;
                    if (reader.GetString(0).EndsWith(BaselineSuffix, StringComparison.Ordinal))
                    {
                        return TargetState.JellyfinSchema;
                    }
                }

                // Only an empty history table: nothing of Jellyfin's has been created yet.
                return tables.Count == 1 && rows == 0 ? TargetState.Empty : TargetState.Other;
            }
        }
    }

    private static ConnectionAssessment Failed(string blocker) => new() { Blockers = [blocker] };

    private static string Unreachable(NpgsqlConnectionStringBuilder builder)
        => string.Create(CultureInfo.InvariantCulture, $"Could not reach PostgreSQL at {builder.Host}:{builder.Port}. Check the host and port, that the server is running, and that this machine is allowed to connect.");

    // Only server-provided texts and the user's own settings are used: the password is never part of a message.
    private static string Describe(PostgresException ex, NpgsqlConnectionStringBuilder builder) => ex.SqlState switch
    {
        PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification
            => string.Create(CultureInfo.InvariantCulture, $"Authentication failed for user '{builder.Username}'. Check the user name and password."),
        PostgresErrorCodes.InvalidCatalogName
            => string.Create(CultureInfo.InvariantCulture, $"The database '{builder.Database}' does not exist. Create it first and make the role its owner."),
        _ => string.Create(CultureInfo.InvariantCulture, $"PostgreSQL refused the connection: {ex.MessageText} (SQLSTATE {ex.SqlState})."),
    };
}
