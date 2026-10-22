using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Reads table relationships from the EF model.
/// </summary>
internal static class CascadeResolver
{
    /// <summary>
    /// Finds the tables that <c>TRUNCATE ... CASCADE</c> of <paramref name="purged"/> also empties:
    /// those referencing the purged tables through a foreign key, directly or transitively.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="purged">The tables the caller asked to purge.</param>
    /// <returns>The additional tables, in discovery order.</returns>
    public static IReadOnlyList<TableRef> FindCascaded(IModel model, IReadOnlyCollection<TableRef> purged)
    {
        var emptied = new HashSet<TableRef>(purged);
        var cascaded = new List<TableRef>();
        var found = true;
        while (found)
        {
            found = false;
            foreach (var entity in model.GetEntityTypes())
            {
                var table = TableOf(entity);
                if (table is null || emptied.Contains(table.Value))
                {
                    continue;
                }

                if (entity.GetForeignKeys().Any(key => TableOf(key.PrincipalEntityType) is { } principal && emptied.Contains(principal)))
                {
                    emptied.Add(table.Value);
                    cascaded.Add(table.Value);
                    found = true;
                }
            }
        }

        return cascaded;
    }

    /// <summary>
    /// Lists the foreign keys of every entity mapped to <paramref name="table"/>.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="table">The table.</param>
    /// <returns>The foreign keys that reference another table.</returns>
    public static IReadOnlyList<ForeignKeyColumns> ForeignKeysOf(IModel model, TableRef table)
    {
        var keys = new List<ForeignKeyColumns>();
        foreach (var entity in model.GetEntityTypes().Where(entity => TableOf(entity) == table))
        {
            var from = StoreObjectIdentifier.Table(table.Name, table.Schema);
            foreach (var key in entity.GetForeignKeys())
            {
                // A self-reference cannot be checked row by row without ordering the rows; the constraint still applies.
                if (TableOf(key.PrincipalEntityType) is not { } principal || principal == table)
                {
                    continue;
                }

                var to = StoreObjectIdentifier.Table(principal.Name, principal.Schema);
                keys.Add(new ForeignKeyColumns(
                    principal,
                    key.Properties.Select(property => property.GetColumnName(from)!).ToList(),
                    key.PrincipalKey.Properties.Select(property => property.GetColumnName(to)!).ToList()));
            }
        }

        return keys;
    }

    private static TableRef? TableOf(IEntityType entity)
        => entity.GetTableName() is { } name ? new TableRef(entity.GetSchema(), name) : null;
}
