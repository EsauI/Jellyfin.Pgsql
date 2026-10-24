using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// The database settings the server currently uses, without the password.
/// </summary>
public class CurrentSettings
{
    /// <summary>Gets or sets the configured database type.</summary>
    [JsonPropertyName("databaseType")]
    public string DatabaseType { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this plugin's provider is the active one.</summary>
    [JsonPropertyName("usesPostgreSql")]
    public bool UsesPostgreSql { get; set; }

    /// <summary>Gets or sets the server host.</summary>
    [JsonPropertyName("host")]
    public string? Host { get; set; }

    /// <summary>Gets or sets the server port.</summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 5432;

    /// <summary>Gets or sets the database name.</summary>
    [JsonPropertyName("database")]
    public string? Database { get; set; }

    /// <summary>Gets or sets the login role.</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>Gets or sets a value indicating whether a password is stored.</summary>
    [JsonPropertyName("hasPassword")]
    public bool HasPassword { get; set; }

    /// <summary>Gets or sets a value indicating whether this server finished its first-run wizard.</summary>
    [JsonPropertyName("serverSetUp")]
    public bool ServerSetUp { get; set; }

    /// <summary>Gets or sets a value indicating whether <c>jellyfin.db</c> exists.</summary>
    [JsonPropertyName("sqliteDatabaseExists")]
    public bool SqliteDatabaseExists { get; set; }

    /// <summary>Gets or sets what switching back to SQLite means, including how old <c>jellyfin.db</c> is; <see langword="null"/> when there is no SQLite database.</summary>
    [JsonPropertyName("switchBackNotice")]
    public string? SwitchBackNotice { get; set; }

    /// <summary>Gets or sets the warnings to show on the page.</summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; set; } = [];
}
