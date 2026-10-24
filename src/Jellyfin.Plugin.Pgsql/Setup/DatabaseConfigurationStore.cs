using System;
using System.IO;
using System.Text;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Plugin.Pgsql.Database;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Npgsql;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// <see cref="IDatabaseConfigurationStore"/> backed by the server configuration manager.
/// </summary>
/// <param name="configurationManager">The server configuration manager.</param>
/// <param name="applicationPaths">The server paths.</param>
public class DatabaseConfigurationStore(IServerConfigurationManager configurationManager, IApplicationPaths applicationPaths) : IDatabaseConfigurationStore
{
    private const string StoreKey = "database";
    private const string PassfileName = "pgsql.pgpass";

    /// <inheritdoc />
    public bool IsServerSetUp => configurationManager.Configuration.IsStartupWizardCompleted;

    /// <inheritdoc />
    public bool SqliteDatabaseExists => File.Exists(Path.Combine(applicationPaths.DataPath, "jellyfin.db"));

    /// <inheritdoc />
    public DateTime? SqliteLastModifiedUtc => SqliteDatabaseExists
        ? File.GetLastWriteTimeUtc(Path.Combine(applicationPaths.DataPath, "jellyfin.db"))
        : null;

    private string PassfilePath => Path.Combine(applicationPaths.ConfigurationDirectoryPath, PassfileName);

    /// <inheritdoc />
    public DatabaseConfigurationOptions Read() => configurationManager.GetConfiguration<DatabaseConfigurationOptions>(StoreKey);

    /// <inheritdoc />
    public string? ReadPassword()
    {
        var stored = TryParse(Read().CustomProviderOptions?.ConnectionString);
        if (!string.IsNullOrEmpty(stored?.Password))
        {
            return stored.Password;
        }

        if (stored is null || string.IsNullOrWhiteSpace(stored.Host))
        {
            return null;
        }

        try
        {
            var path = string.IsNullOrEmpty(stored.Passfile) ? PassfilePath : stored.Passfile;
            var hosts = stored.Host.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var username = stored.Username ?? string.Empty;
            return PassfileFormat.ReadPassword(File.ReadAllText(path), hosts, stored.Port, DatabaseNames.Resolve(stored.Database, username)!, username);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void SavePostgreSql(string connectionString)
    {
        var existing = Read();
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        // database.xml can be read by any signed-in user through the server API, so the password lives in a passfile.
        // The new passfile is staged first and only moved into place once database.xml is saved, so a failure in
        // between leaves the previous working pair.
        string? staged = null;
        try
        {
            if (!string.IsNullOrEmpty(builder.Password))
            {
                staged = StagePassfile(builder);
                builder.Remove("Password");
                builder.Passfile = PassfilePath;
            }

            var provider = new CustomDatabaseOptions
            {
                PluginName = Plugin.PluginName,

                // The .dll is required: Jellyfin swaps the extension to load the assembly.
                PluginAssembly = "Jellyfin.Plugin.Pgsql.dll",
                ConnectionString = builder.ConnectionString,
            };
            foreach (var option in existing.CustomProviderOptions?.Options ?? [])
            {
                provider.Options.Add(option);
            }

            configurationManager.SaveConfiguration(
                StoreKey,
                new DatabaseConfigurationOptions
                {
                    DatabaseType = "PLUGIN_PROVIDER",
                    LockingBehavior = existing.LockingBehavior,
                    CustomProviderOptions = provider,
                });

            if (staged is not null)
            {
                try
                {
                    File.Move(staged, PassfilePath, overwrite: true);
                    staged = null;
                }
                catch
                {
                    configurationManager.SaveConfiguration(StoreKey, existing);
                    throw;
                }
            }
        }
        finally
        {
            if (staged is not null)
            {
                File.Delete(staged);
            }
        }
    }

    /// <inheritdoc />
    public void SaveSqlite()
    {
        var existing = Read();
        configurationManager.SaveConfiguration(
            StoreKey,
            new DatabaseConfigurationOptions
            {
                DatabaseType = "Jellyfin-SQLite",
                LockingBehavior = existing.LockingBehavior,

                // Jellyfin only reads these for PLUGIN_PROVIDER (the SQLite provider looks at its own option keys only),
                // so keeping them lets the page pre-fill the form when the user switches to PostgreSQL again.
                CustomProviderOptions = existing.CustomProviderOptions,
            });
    }

    private static NpgsqlConnectionStringBuilder? TryParse(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private string StagePassfile(NpgsqlConnectionStringBuilder connection)
    {
        var content = new StringBuilder();
        foreach (var host in (connection.Host ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            content.Append(PassfileFormat.FormatEntry(host, connection.Port, connection.Username ?? string.Empty, connection.Password!)).Append('\n');
        }

        var path = PassfilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Owner-only from the first byte, then an atomic swap so readers never see a partial file.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temporary, options))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content.ToString());
                stream.Write(bytes);
            }

            return temporary;
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }
}
