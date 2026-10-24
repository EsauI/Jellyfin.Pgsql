using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// Reads and writes the <c>.pgpass</c> line format (<c>host:port:database:username:password</c>) that Npgsql understands:
/// <c>\</c> and <c>:</c> are escaped with a backslash, and lines starting with <c>#</c> are comments.
/// </summary>
internal static class PassfileFormat
{
    private const char Separator = ':';
    private const char Escape = '\\';

    /// <summary>
    /// Formats one entry that matches every database on the host, so the plugin's maintenance connections work too.
    /// </summary>
    /// <param name="host">The host.</param>
    /// <param name="port">The port.</param>
    /// <param name="username">The login role.</param>
    /// <param name="password">The password.</param>
    /// <returns>The line, without a line break.</returns>
    public static string FormatEntry(string host, int port, string username, string password)
        => string.Join(
            Separator,
            Quote(host),
            port.ToString(CultureInfo.InvariantCulture),
            "*",
            Quote(username),
            Quote(password));

    /// <summary>
    /// Reads the password of the first entry.
    /// </summary>
    /// <param name="content">The file content.</param>
    /// <returns>The password, or <see langword="null"/> when the file has no entry.</returns>
    public static string? ReadPassword(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            var fields = Split(trimmed);
            if (fields.Count == 5)
            {
                return fields[4];
            }
        }

        return null;
    }

    private static string Quote(string value)
        => value.Replace("\\", "\\\\", System.StringComparison.Ordinal).Replace(":", "\\:", System.StringComparison.Ordinal);

    private static List<string> Split(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == Escape && i + 1 < line.Length)
            {
                current.Append(line[++i]);
            }
            else if (c == Separator)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
