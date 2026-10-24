using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// Connects to a PostgreSQL target and reports what the plugin needs to know about it.
/// </summary>
public interface IConnectionChecker
{
    /// <summary>
    /// Checks the target.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The facts found; <see cref="ConnectionAssessment.Blockers"/> explains a failed connection.</returns>
    Task<ConnectionAssessment> CheckAsync(string connectionString, CancellationToken cancellationToken);
}
