using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// The outcome of a setup action that changes <c>database.xml</c>.
/// </summary>
/// <param name="Succeeded">Whether <c>database.xml</c> was written and the server should restart.</param>
/// <param name="Message">What happened, or why nothing was written.</param>
public record SetupResult(
    [property: JsonPropertyName("succeeded")] bool Succeeded,
    [property: JsonPropertyName("message")] string Message);
