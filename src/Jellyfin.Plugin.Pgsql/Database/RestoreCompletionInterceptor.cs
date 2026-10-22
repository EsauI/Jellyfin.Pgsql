using System;
using System.Data.Common;
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
/// they are held in memory and inserted again. The state belongs to the purging context only. The purge, the
/// restore save and the follow-up run in one transaction, committed only when all of them succeeded: the server
/// never saves the context again after a restore, so a partial result could not be repaired later.
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
    /// Makes the purge about to run part of a transaction that also covers the next save of
    /// <paramref name="context"/>: the transaction the caller opened, or one begun here.
    /// </summary>
    /// <param name="context">The purging context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state of the pending restore.</returns>
    public async Task<PendingRestore> BeginAsync(DbContext context, CancellationToken cancellationToken)
    {
        var current = context.Database.CurrentTransaction;
        if (_pending.TryGetValue(context, out var existing))
        {
            if (current is not null && ReferenceEquals(existing.Transaction, current))
            {
                return existing;
            }

            // Left over from an earlier use of a pooled context: its transaction is gone.
            _pending.Remove(context);
        }

        var owns = current is null;
        var transaction = current ?? await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var pending = new PendingRestore(transaction, owns);
        _pending.Add(context, pending);
        return pending;
    }

    /// <summary>
    /// Gives up a pending restore: rolls back the transaction when the provider began it, and forgets the state.
    /// </summary>
    /// <param name="context">The purging context.</param>
    /// <returns>A task that completes when the state is gone.</returns>
    public async Task AbortAsync(DbContext context)
    {
        if (!_pending.TryGetValue(context, out var pending))
        {
            return;
        }

        _pending.Remove(context);
        if (!pending.OwnsTransaction || !ReferenceEquals(context.Database.CurrentTransaction, pending.Transaction))
        {
            return;
        }

        try
        {
            await pending.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await pending.Transaction.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // The original failure is what matters; a transaction that cannot be rolled back dies with its connection.
            LogRollbackFailed(ex);
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

    /// <inheritdoc/>
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context)
        {
            AbortAsync(context).GetAwaiter().GetResult();
        }

        base.SaveChangesFailed(eventData);
    }

    /// <inheritdoc/>
    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is { } context)
        {
            await AbortAsync(context).ConfigureAwait(false);
        }

        await base.SaveChangesFailedAsync(eventData, cancellationToken).ConfigureAwait(false);
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
        if (!ReferenceEquals(context.Database.CurrentTransaction, pending.Transaction))
        {
            // The purge's transaction ended without a save (a pooled context was reset): there is nothing to complete.
            _pending.Remove(context);
            return;
        }

        int skipped;
        try
        {
            skipped = await ApplyAsync(context, pending, cancellationToken).ConfigureAwait(false);
            if (pending.OwnsTransaction)
            {
                await pending.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Purge, restore save and follow-up are one unit: undo them all and let the failure surface.
            await AbortAsync(context).ConfigureAwait(false);
            throw;
        }

        _pending.Remove(context);
        if (pending.OwnsTransaction)
        {
            await pending.Transaction.DisposeAsync().ConfigureAwait(false);
        }

        if (pending.Snapshots.Count > 0)
        {
            LogRestored(pending.Snapshots.Count, skipped);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restored rows of {Tables} table(s) that the purge emptied but the restore did not list; skipped {Orphans} row(s) whose parent row no longer exists")]
    private partial void LogRestored(int tables, int orphans);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The restore transaction could not be rolled back; it ends with its connection")]
    private partial void LogRollbackFailed(Exception exception);
}
