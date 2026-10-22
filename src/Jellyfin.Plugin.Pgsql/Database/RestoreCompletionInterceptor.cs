using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Completes a restore after the save that follows a purge: puts back the rows the purge emptied without
/// being asked to, then moves every identity sequence past the highest stored id.
/// </summary>
/// <remarks>
/// Jellyfin restores a backup by purging the database and inserting the rows with their original ids in a
/// single save. PostgreSQL does not advance a sequence for explicit ids, so without the second step the next
/// insert would reuse an id that already exists. <c>TRUNCATE ... CASCADE</c> also empties tables that the
/// caller did not list (Jellyfin's table list has no entry for <c>HomeSection</c>); SQLite keeps those rows, so
/// they are held in memory and inserted again. The state belongs to the purging context only, and is kept
/// until the follow-up has succeeded.
/// </remarks>
internal sealed partial class RestoreCompletionInterceptor(ILogger logger) : SaveChangesInterceptor
{
    private const string ResynchronizeSql =
        """
        DO $$
        DECLARE
            col record;
        BEGIN
            FOR col IN
                SELECT table_schema, table_name, column_name
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND (is_identity = 'YES' OR column_default LIKE 'nextval(%')
            LOOP
                EXECUTE format(
                    'SELECT setval(pg_get_serial_sequence(%L, %L), COALESCE((SELECT MAX(%I) FROM %I.%I), 0) + 1, false)',
                    format('%I.%I', col.table_schema, col.table_name),
                    col.column_name,
                    col.column_name,
                    col.table_schema,
                    col.table_name);
            END LOOP;
        END $$;
        """;

    private readonly ConditionalWeakTable<DbContext, PendingRestore> _pending = new();

    /// <summary>
    /// Requests the follow-up after the next successful save of <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The purging context.</param>
    /// <param name="snapshots">Rows the purge emptied without being asked to.</param>
    public void MarkPending(DbContext context, System.Collections.Generic.IEnumerable<TableSnapshot> snapshots)
    {
        var pending = _pending.GetValue(context, static _ => new PendingRestore());
        lock (pending)
        {
            foreach (var snapshot in snapshots)
            {
                // A later purge sees the tables already emptied; the first snapshot holds the real rows.
                pending.Snapshots.TryAdd(snapshot.Table, snapshot);
            }
        }
    }

    /// <inheritdoc/>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var pending))
        {
            // The server saves asynchronously and has no synchronization context; this path exists for completeness.
            CompleteAsync(context, pending, CancellationToken.None).GetAwaiter().GetResult();
        }

        return base.SavedChanges(eventData, result);
    }

    /// <inheritdoc/>
    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var pending))
        {
            await CompleteAsync(context, pending, cancellationToken).ConfigureAwait(false);
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ApplyAsync(DbContext context, PendingRestore pending, CancellationToken cancellationToken)
    {
        var skipped = pending.Snapshots.Count > 0
            ? await TableSnapshotter.RestoreAsync(context, pending.Snapshots.Values, cancellationToken).ConfigureAwait(false)
            : 0;

#pragma warning disable EF1002 // The statement is a constant.
        await context.Database.ExecuteSqlRawAsync(ResynchronizeSql, cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
        return skipped;
    }

    private async Task CompleteAsync(DbContext context, PendingRestore pending, CancellationToken cancellationToken)
    {
        int skipped;
        if (context.Database.CurrentTransaction is not null)
        {
            skipped = await ApplyAsync(context, pending, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // One transaction, so a failure leaves nothing half-restored and the whole follow-up can run again.
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                skipped = await ApplyAsync(context, pending, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Only now is the work done; if anything above threw, the state stays and the next save retries it.
        _pending.Remove(context);
        if (pending.Snapshots.Count > 0)
        {
            LogRestored(pending.Snapshots.Count, skipped);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restored rows of {Tables} table(s) that the purge emptied but the restore did not list; skipped {Orphans} row(s) whose parent row no longer exists")]
    private partial void LogRestored(int tables, int orphans);
}
