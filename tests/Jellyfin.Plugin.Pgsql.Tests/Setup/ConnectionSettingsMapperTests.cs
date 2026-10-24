using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Plugin.Pgsql.Setup;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Setup;

public class ConnectionSettingsMapperTests
{
    [Fact]
    public void ToCurrent_ParsesTheStoredConnectionString()
    {
        var current = ConnectionSettingsMapper.ToCurrent(Postgres("Host=db.local;Port=6543;Database=media;Username=jf;Password=s3cret"));

        Assert.True(current.UsesPostgreSql);
        Assert.Equal("db.local", current.Host);
        Assert.Equal(6543, current.Port);
        Assert.Equal("media", current.Database);
        Assert.Equal("jf", current.Username);
        Assert.True(current.HasPassword);
    }

    [Fact]
    public void ToCurrent_NeverExposesThePassword()
    {
        var current = ConnectionSettingsMapper.ToCurrent(Postgres("Host=db;Username=jf;Password=s3cret-value"));

        var json = System.Text.Json.JsonSerializer.Serialize(current);
        Assert.DoesNotContain("s3cret-value", json, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ToCurrent_ResolvesTheDatabaseFromTheUsernameWhenOmitted()
    {
        var current = ConnectionSettingsMapper.ToCurrent(Postgres("Host=db;Username=jellyfin"));

        Assert.Equal("jellyfin", current.Database);
        Assert.False(current.HasPassword);
    }

    [Fact]
    public void ToCurrent_ForSqlite_ReportsNoPostgreSql()
    {
        var current = ConnectionSettingsMapper.ToCurrent(new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-SQLite" });

        Assert.False(current.UsesPostgreSql);
        Assert.Equal("Jellyfin-SQLite", current.DatabaseType);
        Assert.Null(current.Host);
    }

    [Fact]
    public void Build_KeepsTheStoredPasswordWhenNoneIsSubmitted()
    {
        var built = new NpgsqlConnectionStringBuilder(ConnectionSettingsMapper.Build(
            new ConnectionSettings { Host = "new-host", Port = 5433, Database = "media", Username = "jf" },
            "Host=old;Username=jf;Password=stored"));

        Assert.Equal("new-host", built.Host);
        Assert.Equal(5433, built.Port);
        Assert.Equal("media", built.Database);
        Assert.Equal("stored", built.Password);
    }

    [Fact]
    public void Build_UsesTheSubmittedPassword()
    {
        var built = new NpgsqlConnectionStringBuilder(ConnectionSettingsMapper.Build(
            new ConnectionSettings { Host = "h", Username = "jf", Password = "fresh" },
            "Host=old;Username=jf;Password=stored"));

        Assert.Equal("fresh", built.Password);
    }

    [Fact]
    public void Build_UsesThePasswordKeptByTheStoreOverAStoredPassfileReference()
    {
        var built = new NpgsqlConnectionStringBuilder(ConnectionSettingsMapper.Build(
            new ConnectionSettings { Host = "h", Username = "jf" },
            "Host=old;Username=jf;Passfile=/config/pgsql.pgpass",
            "from-the-passfile"));

        Assert.Equal("from-the-passfile", built.Password);
        Assert.True(string.IsNullOrEmpty(built.Passfile));
    }

    [Fact]
    public void Validate_RejectsAPasswordWithLineBreaks_BecauseTheyWouldCorruptThePassfile()
    {
        Assert.NotNull(ConnectionSettingsMapper.Validate(new ConnectionSettings { Host = "db", Username = "jf", Password = "a\nb" }));
    }

    [Theory]
    [InlineData(null, "jf", 5432)]
    [InlineData("db", null, 5432)]
    [InlineData("db", "jf", 0)]
    [InlineData("db", "jf", 70000)]
    public void Validate_RejectsIncompleteSettings(string? host, string? username, int port)
    {
        Assert.NotNull(ConnectionSettingsMapper.Validate(new ConnectionSettings { Host = host, Username = username, Port = port }));
    }

    [Fact]
    public void Validate_AcceptsCompleteSettings()
    {
        Assert.Null(ConnectionSettingsMapper.Validate(new ConnectionSettings { Host = "db", Username = "jf", Port = 5432 }));
    }

    private static DatabaseConfigurationOptions Postgres(string connectionString) => new()
    {
        DatabaseType = "PLUGIN_PROVIDER",
        CustomProviderOptions = new CustomDatabaseOptions { PluginName = "PostgreSQL", PluginAssembly = "Jellyfin.Plugin.Pgsql.dll", ConnectionString = connectionString },
    };
}
