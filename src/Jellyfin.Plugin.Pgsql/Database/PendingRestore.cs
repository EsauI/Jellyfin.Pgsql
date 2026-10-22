using System.Collections.Generic;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// What a purging context still has to do after the save that follows its purge.
/// </summary>
internal sealed class PendingRestore
{
    /// <summary>
    /// Gets the rows of tables the purge emptied without being asked to, keyed by table.
    /// </summary>
    public Dictionary<TableRef, TableSnapshot> Snapshots { get; } = [];
}
