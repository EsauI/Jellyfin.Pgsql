using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

/// <summary>
/// Reproduces what Jellyfin 12.2 <c>BackupService.RestoreBackupAsync</c> does to the database.
/// </summary>
internal static class BackupServiceRestore
{
    /// <summary>
    /// The table list the server hands to <c>PurgeDatabase</c>: one schema-qualified name per <c>DbSet</c>
    /// property of the context. Tables without a <c>DbSet</c> (such as <c>HomeSection</c>) are not in it.
    /// </summary>
    /// <param name="context">The context whose model is used.</param>
    /// <returns>The table names.</returns>
    public static IReadOnlyList<string> TableNames(JellyfinDbContext context)
        => typeof(JellyfinDbContext).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType.IsAssignableTo(typeof(System.Linq.IQueryable)))
            .Select(property => context.Model.FindEntityType(property.PropertyType.GetGenericArguments()[0])!.GetSchemaQualifiedTableName()!)
            .ToList();

    /// <summary>
    /// Purges the database like the server does, adds the rows with their original ids and saves once.
    /// </summary>
    /// <param name="database">The database to restore into.</param>
    /// <param name="rows">The rows of the backup archive.</param>
    /// <param name="token">The cancellation token.</param>
    /// <param name="afterPurge">An optional step run between the purge and the save.</param>
    /// <returns>A task that completes when the restore has been saved.</returns>
    public static async Task RestoreAsync(
        TestDatabase database,
        IEnumerable<object> rows,
        CancellationToken token,
        Func<JellyfinDbContext, Task>? afterPurge = null)
    {
        await using var restore = database.CreateContext();
        await database.Provider.PurgeDatabase(restore, TableNames(restore));
        if (afterPurge is not null)
        {
            await afterPurge(restore);
        }

        foreach (var row in rows)
        {
            restore.Add(row);
        }

        await restore.SaveChangesAsync(token);
    }
}
