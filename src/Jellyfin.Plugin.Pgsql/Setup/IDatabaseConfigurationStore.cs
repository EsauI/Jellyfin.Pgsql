using System;
using Jellyfin.Database.Implementations.DbConfiguration;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// Reads and writes the server's <c>database.xml</c>, the single source of truth for the database connection.
/// </summary>
public interface IDatabaseConfigurationStore
{
    /// <summary>Gets a value indicating whether the server finished its first-run wizard.</summary>
    bool IsServerSetUp { get; }

    /// <summary>Gets a value indicating whether <c>jellyfin.db</c> exists.</summary>
    bool SqliteDatabaseExists { get; }

    /// <summary>Gets when <c>jellyfin.db</c> was last modified (UTC), or <see langword="null"/> when it does not exist.</summary>
    DateTime? SqliteLastModifiedUtc { get; }

    /// <summary>
    /// Reads the current database configuration.
    /// </summary>
    /// <returns>The configuration.</returns>
    DatabaseConfigurationOptions Read();

    /// <summary>
    /// Reads the password kept for the PostgreSQL connection.
    /// </summary>
    /// <returns>The password, or <see langword="null"/> when none is kept.</returns>
    string? ReadPassword();

    /// <summary>
    /// Points the server at this plugin's PostgreSQL provider.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    void SavePostgreSql(string connectionString);

    /// <summary>
    /// Points the server back at SQLite.
    /// </summary>
    void SaveSqlite();
}
