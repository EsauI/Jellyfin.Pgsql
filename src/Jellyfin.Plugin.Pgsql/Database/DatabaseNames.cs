using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Quotes identifiers and derives the names of the databases a backup or restore creates beside the live one.
/// </summary>
/// <remarks>
/// PostgreSQL silently truncates identifiers to 63 bytes, which would let two derived names collide or collide with
/// the live database. A derived name that would not fit is shortened deterministically instead: the live name is cut
/// on a character boundary and a hash of the complete name is added, so different live names stay distinct.
/// </remarks>
internal static class DatabaseNames
{
    internal const int MaxIdentifierBytes = 63;
    private const int HashLength = 8;

    /// <summary>
    /// Quotes an identifier for use in SQL.
    /// </summary>
    /// <param name="identifier">The unquoted name.</param>
    /// <returns>The name in double quotes, with embedded double quotes doubled.</returns>
    public static string Quote(string identifier)
        => string.Create(CultureInfo.InvariantCulture, $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");

    /// <summary>
    /// Gets the name of the database that holds a backup.
    /// </summary>
    /// <param name="database">The live database name.</param>
    /// <param name="key">The backup key.</param>
    /// <returns>The backup database name.</returns>
    public static string Backup(string database, string key) => Derive(database, "backup", key);

    /// <summary>
    /// Gets the name of the database a restore is built in before it replaces the live one.
    /// </summary>
    /// <param name="database">The live database name.</param>
    /// <param name="stamp">The restore stamp.</param>
    /// <returns>The restore database name.</returns>
    public static string Restore(string database, string stamp) => Derive(database, "restore", stamp);

    /// <summary>
    /// Gets the name the replaced live database is kept under.
    /// </summary>
    /// <param name="database">The live database name.</param>
    /// <param name="stamp">The restore stamp.</param>
    /// <returns>The name of the kept database.</returns>
    public static string Failed(string database, string stamp) => Derive(database, "failed", stamp);

    private static string Derive(string database, string kind, string suffix)
    {
        var tail = $"_{kind}_{suffix}";
        if (Encoding.UTF8.GetByteCount(database) + Encoding.UTF8.GetByteCount(tail) <= MaxIdentifierBytes)
        {
            return database + tail;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(database)))[..HashLength];
        var budget = Math.Max(0, MaxIdentifierBytes - Encoding.UTF8.GetByteCount(tail) - HashLength - 1);
        return $"{TruncateToBytes(database, budget)}_{hash}{tail}";
    }

    private static string TruncateToBytes(string value, int maxBytes)
    {
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            used += rune.Utf8SequenceLength;
            if (used > maxBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }
}
