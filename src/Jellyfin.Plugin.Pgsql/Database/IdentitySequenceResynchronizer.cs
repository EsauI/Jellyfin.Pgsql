using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Moves every identity sequence past the highest stored id after the save that follows a purge.
/// </summary>
/// <remarks>
/// Jellyfin restores a backup by purging the database and inserting the rows with their original ids in a
/// single save. PostgreSQL does not advance a sequence for explicit ids, so without this the next insert
/// would reuse an id that already exists.
/// </remarks>
internal sealed class IdentitySequenceResynchronizer : SaveChangesInterceptor
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

    private int _pending;

    /// <summary>
    /// Requests a resynchronisation after the next successful save.
    /// </summary>
    public void MarkPending() => Interlocked.Exchange(ref _pending, 1);

    /// <inheritdoc/>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is not null && Interlocked.Exchange(ref _pending, 0) == 1)
        {
            eventData.Context.Database.ExecuteSqlRaw(ResynchronizeSql);
        }

        return base.SavedChanges(eventData, result);
    }

    /// <inheritdoc/>
    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null && Interlocked.Exchange(ref _pending, 0) == 1)
        {
            await eventData.Context.Database.ExecuteSqlRawAsync(ResynchronizeSql, cancellationToken).ConfigureAwait(false);
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }
}
