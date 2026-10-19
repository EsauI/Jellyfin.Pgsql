using System;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Maps <see cref="DateTime"/> values to PostgreSQL <c>timestamp with time zone</c>, which Npgsql only accepts
/// with <see cref="DateTimeKind.Utc"/>. Jellyfin stores UTC instants, so values of unspecified kind are
/// interpreted as UTC rather than shifted.
/// </summary>
internal sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UtcDateTimeConverter"/> class.
    /// </summary>
    public UtcDateTimeConverter()
        : base(value => ToUtc(value), value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
    {
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };
}
