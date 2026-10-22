using System.Collections.Generic;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// The rows of a table held in memory, with what is needed to put them back.
/// </summary>
/// <param name="Table">The table.</param>
/// <param name="Columns">The column names, in the order of each row's values.</param>
/// <param name="DataTypeNames">The PostgreSQL type of each column.</param>
/// <param name="Rows">The rows; database nulls are <see cref="System.DBNull"/>.</param>
/// <param name="ForeignKeys">The foreign keys of the table.</param>
internal sealed record TableSnapshot(
    TableRef Table,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> DataTypeNames,
    IReadOnlyList<object[]> Rows,
    IReadOnlyList<ForeignKeyColumns> ForeignKeys);
