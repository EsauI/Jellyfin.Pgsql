using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Copies the rows of tables into memory and puts them back, over the connection of a context.
/// </summary>
/// <remarks>
/// Values are carried as the driver reads them together with their PostgreSQL type name, so ids, nulls,
/// timestamps and uuids come back exactly as stored.
/// </remarks>
internal static class TableSnapshotter
{
    /// <summary>
    /// Reads every row of <paramref name="table"/>.
    /// </summary>
    /// <param name="context">The context whose connection (and current transaction) is used.</param>
    /// <param name="table">The table.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The snapshot.</returns>
    public static async Task<TableSnapshot> CaptureAsync(DbContext context, TableRef table, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = CreateCommand(context);
            await using (command.ConfigureAwait(false))
            {
#pragma warning disable CA2100 // The table name comes from the EF model and is quoted.
                command.CommandText = "SELECT * FROM " + table.Quoted;
#pragma warning restore CA2100
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                    var types = Enumerable.Range(0, reader.FieldCount).Select(reader.GetDataTypeName).ToList();
                    var rows = new List<object[]>();
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var values = new object[reader.FieldCount];
                        reader.GetValues(values);
                        rows.Add(values);
                    }

                    return new TableSnapshot(table, columns, types, rows, CascadeResolver.ForeignKeysOf(context.Model, table));
                }
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Inserts the rows of the snapshots, skipping rows that reference a parent row that no longer exists.
    /// </summary>
    /// <param name="context">The context whose connection (and current transaction) is used.</param>
    /// <param name="snapshots">The snapshots.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of rows skipped because their parent is missing.</returns>
    public static async Task<int> RestoreAsync(DbContext context, IEnumerable<TableSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var skipped = 0;
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var snapshot in InDependencyOrder(snapshots))
            {
                foreach (var row in snapshot.Rows)
                {
                    var command = CreateCommand(context);
                    await using (command.ConfigureAwait(false))
                    {
                        BuildInsert(command, snapshot, row);
                        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                        {
                            skipped++;
                        }
                    }
                }
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return skipped;
    }

    private static NpgsqlCommand CreateCommand(DbContext context)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var command = connection.CreateCommand();
        command.Transaction = (NpgsqlTransaction?)context.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    // INSERT ... SELECT ... WHERE EXISTS (parent) inserts nothing when a referenced row is gone.
    private static void BuildInsert(NpgsqlCommand command, TableSnapshot snapshot, object[] row)
    {
        var sql = new StringBuilder("INSERT INTO ").Append(snapshot.Table.Quoted).Append(" (");
        sql.AppendJoin(", ", snapshot.Columns.Select(TableRef.Quote)).Append(") SELECT ");
        sql.AppendJoin(", ", snapshot.Columns.Select((_, index) => Parameter(index)));
        for (var index = 0; index < snapshot.Columns.Count; index++)
        {
            command.Parameters.Add(new NpgsqlParameter(ParameterName(index), null)
            {
                DataTypeName = snapshot.DataTypeNames[index],
                Value = row[index],
            });
        }

        var checks = new List<string>();
        foreach (var key in snapshot.ForeignKeys)
        {
            var indexes = key.Columns.Select(column => snapshot.Columns.ToList().IndexOf(column)).ToList();

            // A key with a null column is not enforced by PostgreSQL (MATCH SIMPLE), so there is nothing to check.
            if (indexes.Any(index => index < 0 || row[index] is DBNull))
            {
                continue;
            }

            var match = string.Join(" AND ", indexes.Select((index, position) => TableRef.Quote(key.PrincipalColumns[position]) + " = " + Parameter(index)));
            checks.Add($"EXISTS (SELECT 1 FROM {key.Principal.Quoted} WHERE {match})");
        }

        if (checks.Count > 0)
        {
            sql.Append(" WHERE ").AppendJoin(" AND ", checks);
        }

#pragma warning disable CA2100 // Identifiers come from the database catalog and are quoted; values are parameters.
        command.CommandText = sql.ToString();
#pragma warning restore CA2100
    }

    private static string ParameterName(int index) => string.Create(CultureInfo.InvariantCulture, $"p{index}");

    private static string Parameter(int index) => "@" + ParameterName(index);

    // Parents first, so the existence checks of children see the rows restored before them.
    private static List<TableSnapshot> InDependencyOrder(IEnumerable<TableSnapshot> snapshots)
    {
        var remaining = snapshots.ToList();
        var ordered = new List<TableSnapshot>();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(candidate =>
                !candidate.ForeignKeys.Any(key => remaining.Any(other => other != candidate && other.Table == key.Principal)))
                ?? remaining[0];
            ordered.Add(next);
            remaining.Remove(next);
        }

        return ordered;
    }
}
