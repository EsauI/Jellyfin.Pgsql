namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// What a short-lived connection learned about the configured database.
/// </summary>
/// <param name="Exists">Whether the database exists on the server.</param>
/// <param name="HasTables">Whether it contains any user tables.</param>
internal readonly record struct ServerProbe(bool Exists, bool HasTables);
