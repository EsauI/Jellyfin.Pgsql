using System;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Plugin.Pgsql.Database;
using Npgsql;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// Converts between <c>database.xml</c> and the settings of the configuration page.
/// </summary>
internal static class ConnectionSettingsMapper
{
    private const string PluginProvider = "PLUGIN_PROVIDER";

    /// <summary>
    /// Describes the stored configuration for the page. The password is never copied.
    /// </summary>
    /// <param name="options">The stored database configuration.</param>
    /// <returns>The settings without the password.</returns>
    public static CurrentSettings ToCurrent(DatabaseConfigurationOptions options)
    {
        var current = new CurrentSettings { DatabaseType = options.DatabaseType };
        var stored = options.CustomProviderOptions?.ConnectionString;
        var usesPlugin = string.Equals(options.DatabaseType, PluginProvider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(options.CustomProviderOptions?.PluginName, Plugin.PluginName, StringComparison.Ordinal);
        current.UsesPostgreSql = usesPlugin;

        var connection = Parse(stored);
        if (connection is not null)
        {
            current.Host = connection.Host;
            current.Port = connection.Port;
            current.Username = connection.Username;
            current.Database = DatabaseNames.Resolve(connection.Database, connection.Username);
            current.HasPassword = !string.IsNullOrEmpty(connection.Password);
        }

        return current;
    }

    /// <summary>
    /// Builds the connection string to store. Settings the page does not show (SSL mode and so on) are kept from the
    /// stored string, and the stored password is kept when none is submitted.
    /// </summary>
    /// <param name="settings">The submitted settings.</param>
    /// <param name="storedConnectionString">The connection string currently stored, if any.</param>
    /// <param name="storedPassword">The password kept by the store, used when none is submitted.</param>
    /// <returns>The new connection string.</returns>
    public static string Build(ConnectionSettings settings, string? storedConnectionString, string? storedPassword = null)
    {
        var stored = Parse(storedConnectionString);
        var builder = new NpgsqlConnectionStringBuilder(stored?.ConnectionString)
        {
            Host = settings.Host?.Trim(),
            Port = settings.Port,
            Username = settings.Username?.Trim(),
            Database = DatabaseNames.Resolve(settings.Database?.Trim(), settings.Username?.Trim()),
        };
        var password = !string.IsNullOrEmpty(settings.Password) ? settings.Password : storedPassword;
        if (!string.IsNullOrEmpty(password))
        {
            // The store moves the password into a passfile when it saves; a stale reference must not outlive it.
            builder.Password = password;
            builder.Remove("Passfile");
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Checks that the submitted settings are complete.
    /// </summary>
    /// <param name="settings">The submitted settings.</param>
    /// <returns>A message naming the first problem, or <see langword="null"/> when the settings are usable.</returns>
    public static string? Validate(ConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            return "Enter the host name or address of the PostgreSQL server.";
        }

        if (settings.Port is < 1 or > 65535)
        {
            return "The port must be between 1 and 65535.";
        }

        if (string.IsNullOrWhiteSpace(settings.Username))
        {
            return "Enter the PostgreSQL user name.";
        }

        if (settings.Password?.Contains('\n', StringComparison.Ordinal) == true || settings.Password?.Contains('\r', StringComparison.Ordinal) == true)
        {
            return "The password cannot contain line breaks.";
        }

        var database = DatabaseNames.Resolve(settings.Database?.Trim(), settings.Username);
        return System.Text.Encoding.UTF8.GetByteCount(database!) > DatabaseNames.MaxIdentifierBytes
            ? $"The database name must be at most {DatabaseNames.MaxIdentifierBytes} bytes."
            : null;
    }

    private static NpgsqlConnectionStringBuilder? Parse(string? connectionString)
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
}
