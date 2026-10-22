using System.Collections.Generic;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// The columns of a foreign key and the columns of the key it references.
/// </summary>
/// <param name="Principal">The referenced table.</param>
/// <param name="Columns">The referencing columns.</param>
/// <param name="PrincipalColumns">The referenced columns, in the same order.</param>
internal sealed record ForeignKeyColumns(TableRef Principal, IReadOnlyList<string> Columns, IReadOnlyList<string> PrincipalColumns);
