using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// The connection settings submitted by the configuration page.
/// </summary>
public class ConnectionSettings
{
    /// <summary>Gets or sets the server host name or address.</summary>
    [JsonPropertyName("host")]
    public string? Host { get; set; }

    /// <summary>Gets or sets the server port.</summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 5432;

    /// <summary>Gets or sets the database name; the user name is used when empty.</summary>
    [JsonPropertyName("database")]
    public string? Database { get; set; }

    /// <summary>Gets or sets the login role.</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>Gets or sets the password; the stored one is kept when empty.</summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }
}
