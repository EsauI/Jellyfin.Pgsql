using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// Implements the actions of the configuration page.
/// </summary>
/// <param name="store">The <c>database.xml</c> store.</param>
/// <param name="checker">The PostgreSQL connection checker.</param>
/// <param name="folders">The plugin folder inspector.</param>
public class GuidedSetupService(IDatabaseConfigurationStore store, IConnectionChecker checker, IPluginFolderInspector folders)
{
    private const string SqliteDatabaseType = "Jellyfin-SQLite";

    /// <summary>
    /// Reads the settings the server currently uses.
    /// </summary>
    /// <returns>The settings without the password, with warnings.</returns>
    public CurrentSettings GetSettings()
    {
        var current = ConnectionSettingsMapper.ToCurrent(store.Read());
        current.ServerSetUp = store.IsServerSetUp;
        current.SqliteDatabaseExists = store.SqliteDatabaseExists;
        current.HasPassword = current.HasPassword || !string.IsNullOrEmpty(store.ReadPassword());

        current.SwitchBackNotice = store.SqliteLastModifiedUtc is { } modified ? SwitchBackNoticeFor(modified) : null;

        var providerFolders = folders.FindProviderFolders();
        if (providerFolders.Count > 1)
        {
            current.Warnings =
            [
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Several {Plugin.PluginName} plugin folders exist ({string.Join(", ", providerFolders)}). Jellyfin loads the database provider from the first one in its own ordering ({providerFolders[0]}), which may be an older version. Remove the folders of the versions you no longer want while Jellyfin is stopped."),
            ];
        }

        return current;
    }

    /// <summary>
    /// Checks the submitted settings against the server without changing anything.
    /// </summary>
    /// <param name="settings">The submitted settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was found and why the settings cannot be applied, if they cannot.</returns>
    public async Task<ConnectionAssessment> TestAsync(ConnectionSettings settings, CancellationToken cancellationToken)
    {
        var (assessment, _) = await AssessAsync(settings, cancellationToken).ConfigureAwait(false);
        return assessment;
    }

    /// <summary>
    /// Writes <c>database.xml</c> for PostgreSQL when the target is usable.
    /// </summary>
    /// <param name="settings">The submitted settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the file was written; the caller restarts the server on success.</returns>
    public async Task<SetupResult> ApplyAsync(ConnectionSettings settings, CancellationToken cancellationToken)
    {
        var (assessment, connectionString) = await AssessAsync(settings, cancellationToken).ConfigureAwait(false);
        if (!assessment.CanApply || connectionString is null)
        {
            return new SetupResult(false, "Nothing was changed. " + string.Join(" ", assessment.Blockers));
        }

        // The very connection string that was checked is the one saved: the store is not read again in between.
        store.SavePostgreSql(connectionString);
        return new SetupResult(true, "Saved. Jellyfin is restarting to use PostgreSQL.");
    }

    /// <summary>
    /// Points the server back at SQLite. <c>jellyfin.db</c> is never touched.
    /// </summary>
    /// <returns>Whether the file was written (the caller restarts the server on success); the message states what a rollback to SQLite means.</returns>
    public SetupResult SwitchToSqlite()
    {
        if (string.Equals(store.Read().DatabaseType, SqliteDatabaseType, StringComparison.OrdinalIgnoreCase))
        {
            return new SetupResult(false, "Nothing was changed: Jellyfin already uses SQLite.");
        }

        if (!store.SqliteDatabaseExists)
        {
            return new SetupResult(false, "Nothing was changed: jellyfin.db was not found, so there is no SQLite database to go back to.");
        }

        store.SaveSqlite();
        return new SetupResult(true, "Saved. Jellyfin is restarting to use SQLite. " + SwitchBackNoticeFor(store.SqliteLastModifiedUtc ?? DateTime.UtcNow));
    }

    private static string SwitchBackNoticeFor(DateTime modifiedUtc)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"Switching back is a rollback, not a sync: jellyfin.db was last modified on {modifiedUtc:yyyy-MM-dd HH:mm} UTC and has not changed since Jellyfin moved to PostgreSQL. Everything changed since then (watch history, new items, users, settings stored in the database) exists only in PostgreSQL and will NOT appear in SQLite. The PostgreSQL data is kept untouched, so you can switch to PostgreSQL again.");

    private async Task<(ConnectionAssessment Assessment, string? ConnectionString)> AssessAsync(ConnectionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var invalid = ConnectionSettingsMapper.Validate(settings);
        if (invalid is not null)
        {
            return (new ConnectionAssessment { Blockers = [invalid] }, null);
        }

        var storedConnectionString = store.Read().CustomProviderOptions?.ConnectionString;
        string? storedPassword = null;
        if (string.IsNullOrEmpty(settings.Password))
        {
            storedPassword = store.ReadPassword();
            if (!ConnectionSettingsMapper.KeepsStoredCredentials(settings, storedConnectionString))
            {
                // The stored password was entered for another server or role; it is never replayed to a changed target.
                if (!string.IsNullOrEmpty(storedPassword))
                {
                    return (new ConnectionAssessment { Blockers = ["Enter the password: the host, port or username changed."] }, null);
                }

                storedPassword = null;
            }
        }

        var connectionString = ConnectionSettingsMapper.Build(settings, storedConnectionString, storedPassword);
        var assessment = await checker.CheckAsync(connectionString, cancellationToken).ConfigureAwait(false);
        if (!assessment.Connected)
        {
            return (assessment, connectionString);
        }

        var blockers = new List<string>(assessment.Blockers);
        if (!assessment.CanCreateDatabase)
        {
            blockers.Add("The PostgreSQL role needs the CREATEDB privilege: the plugin clones the database to back it up. Run ALTER ROLE <role> CREATEDB.");
        }

        if (!assessment.OwnsDatabase)
        {
            blockers.Add("The PostgreSQL role must own the database (or be a superuser): backups and restores clone it. Run ALTER DATABASE <database> OWNER TO <role>.");
        }

        switch (assessment.TargetState)
        {
            case TargetState.Other:
                blockers.Add("The database already contains tables that are not Jellyfin's. Use an empty database.");
                break;
            case TargetState.Empty:
                // Jellyfin seeds an empty database only on the first start of the process; the restart this plugin triggers
                // happens in-process and skips that, so an empty target would leave the server unable to start.
                blockers.Add("The database is empty, and Jellyfin cannot start on an empty database. The current data has to be migrated into it first (the \"Migrate\" action arrives in a later version of this plugin).");
                break;
        }

        assessment.Blockers = blockers;
        return (assessment, connectionString);
    }
}
