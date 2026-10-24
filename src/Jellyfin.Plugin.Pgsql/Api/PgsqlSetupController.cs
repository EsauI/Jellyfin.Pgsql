using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Pgsql.Setup;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Pgsql.Api;

/// <summary>
/// The API behind the plugin's configuration page. Administrators only: it rewrites <c>database.xml</c>.
/// </summary>
/// <param name="setup">The setup service.</param>
/// <param name="systemManager">The system manager used to restart the server.</param>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Pgsql")]
public class PgsqlSetupController(GuidedSetupService setup, ISystemManager systemManager) : ControllerBase
{
    /// <summary>
    /// Gets the database settings the server currently uses. The password is never returned.
    /// </summary>
    /// <returns>The current settings and any warnings.</returns>
    [HttpGet("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CurrentSettings> GetSettings() => setup.GetSettings();

    /// <summary>
    /// Connects with the submitted settings and reports what was found.
    /// </summary>
    /// <param name="settings">The submitted settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The assessment.</returns>
    [HttpPost("Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ConnectionAssessment>> Test([FromBody] ConnectionSettings settings, CancellationToken cancellationToken)
        => await setup.TestAsync(settings, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Writes <c>database.xml</c> for PostgreSQL and restarts the server.
    /// </summary>
    /// <param name="settings">The submitted settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the settings were applied.</returns>
    [HttpPost("Apply")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SetupResult>> Apply([FromBody] ConnectionSettings settings, CancellationToken cancellationToken)
    {
        var result = await setup.ApplyAsync(settings, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            systemManager.Restart();
        }

        return result;
    }

    /// <summary>
    /// Writes <c>database.xml</c> back to SQLite and restarts the server.
    /// </summary>
    /// <returns>Whether the settings were applied.</returns>
    [HttpPost("SwitchToSqlite")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SetupResult> SwitchToSqlite()
    {
        var result = setup.SwitchToSqlite();
        if (result.Succeeded)
        {
            systemManager.Restart();
        }

        return result;
    }
}
