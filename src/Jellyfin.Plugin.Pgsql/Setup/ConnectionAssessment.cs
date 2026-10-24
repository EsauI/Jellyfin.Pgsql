using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// The outcome of checking a PostgreSQL target.
/// </summary>
public class ConnectionAssessment
{
    /// <summary>Gets or sets a value indicating whether the server accepted the connection.</summary>
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    /// <summary>Gets or sets the server version.</summary>
    [JsonPropertyName("serverVersion")]
    public string? ServerVersion { get; set; }

    /// <summary>Gets or sets a value indicating whether the role may create databases (needed for backups and restores).</summary>
    [JsonPropertyName("canCreateDatabase")]
    public bool CanCreateDatabase { get; set; }

    /// <summary>Gets or sets a value indicating whether the role owns the database (needed for backups and restores).</summary>
    [JsonPropertyName("ownsDatabase")]
    public bool OwnsDatabase { get; set; }

    /// <summary>Gets or sets what the database contains.</summary>
    [JsonPropertyName("targetState")]
    public TargetState TargetState { get; set; }

    /// <summary>Gets or sets the reasons this target cannot be used, in plain language.</summary>
    [JsonPropertyName("blockers")]
    public IReadOnlyList<string> Blockers { get; set; } = [];

    /// <summary>Gets a value indicating whether the target can be applied.</summary>
    [JsonPropertyName("canApply")]
    public bool CanApply => Blockers.Count == 0;
}
