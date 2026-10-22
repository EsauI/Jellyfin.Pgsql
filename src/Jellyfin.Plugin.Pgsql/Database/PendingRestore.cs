using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Storage;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// What a purging context still has to do after the save that follows its purge, and the transaction that
/// makes the purge, the restore save and that follow-up one unit.
/// </summary>
internal sealed class PendingRestore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PendingRestore"/> class.
    /// </summary>
    /// <param name="transaction">The transaction the purge ran in.</param>
    /// <param name="ownsTransaction">Whether the provider began <paramref name="transaction"/> and so commits or rolls it back.</param>
    public PendingRestore(IDbContextTransaction transaction, bool ownsTransaction)
    {
        Transaction = transaction;
        OwnsTransaction = ownsTransaction;
    }

    /// <summary>
    /// Gets the transaction the purge ran in.
    /// </summary>
    public IDbContextTransaction Transaction { get; }

    /// <summary>
    /// Gets a value indicating whether the provider began the transaction. A transaction started by the caller is left to the caller.
    /// </summary>
    public bool OwnsTransaction { get; }

    /// <summary>
    /// Gets the rows of tables the purge emptied without being asked to, keyed by table.
    /// </summary>
    public Dictionary<TableRef, TableSnapshot> Snapshots { get; } = [];
}
