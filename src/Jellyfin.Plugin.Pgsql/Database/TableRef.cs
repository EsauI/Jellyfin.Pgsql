using System.Globalization;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Identifies a table by its optional schema and name.
/// </summary>
/// <param name="Schema">The schema, or <c>null</c> for the default one.</param>
/// <param name="Name">The table name.</param>
internal readonly record struct TableRef(string? Schema, string Name)
{
    /// <summary>
    /// Gets the table name quoted for use in a SQL statement.
    /// </summary>
    public string Quoted => Schema is null ? Quote(Name) : Quote(Schema) + "." + Quote(Name);

    /// <summary>
    /// Quotes an identifier for use in a SQL statement.
    /// </summary>
    /// <param name="identifier">The identifier.</param>
    /// <returns>The quoted identifier.</returns>
    public static string Quote(string identifier)
        => string.Create(CultureInfo.InvariantCulture, $"\"{identifier.Replace("\"", "\"\"", System.StringComparison.Ordinal)}\"");
}
