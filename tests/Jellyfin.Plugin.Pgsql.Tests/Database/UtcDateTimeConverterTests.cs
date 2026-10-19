using System;
using Jellyfin.Plugin.Pgsql.Database;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

public class UtcDateTimeConverterTests
{
    private static readonly UtcDateTimeConverter Converter = new();

    [Fact]
    public void ToProvider_KeepsUtcValueUnchanged()
    {
        var value = new DateTime(2026, 3, 1, 12, 30, 0, DateTimeKind.Utc);

        var result = ToProvider(value);

        Assert.Equal(value, result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void ToProvider_ConvertsLocalValueToUtc()
    {
        var value = new DateTime(2026, 3, 1, 12, 30, 0, DateTimeKind.Local);

        var result = ToProvider(value);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(value.ToUniversalTime(), result);
    }

    [Fact]
    public void ToProvider_TreatsUnspecifiedValueAsUtcWithoutShiftingTheClock()
    {
        var value = new DateTime(2026, 3, 1, 12, 30, 0, DateTimeKind.Unspecified);

        var result = ToProvider(value);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(value.Ticks, result.Ticks);
    }

    [Fact]
    public void FromProvider_MarksValueAsUtc()
    {
        var stored = new DateTime(2026, 3, 1, 12, 30, 0, DateTimeKind.Unspecified);

        var result = (DateTime)Converter.ConvertFromProvider(stored)!;

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(stored.Ticks, result.Ticks);
    }

    private static DateTime ToProvider(DateTime value) => (DateTime)Converter.ConvertToProvider(value)!;
}
