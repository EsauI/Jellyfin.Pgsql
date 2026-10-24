using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// What a PostgreSQL database contains, as far as Jellyfin is concerned.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TargetState
{
    /// <summary>The database could not be inspected.</summary>
    Unknown,

    /// <summary>The database has no tables.</summary>
    Empty,

    /// <summary>The database holds the Jellyfin schema created by this plugin.</summary>
    JellyfinSchema,

    /// <summary>The database holds tables that are not the Jellyfin schema.</summary>
    Other,
}
