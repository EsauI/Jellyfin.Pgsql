using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.Pgsql.Setup;
using Jellyfin.Plugin.Pgsql.Tests.Database;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Setup;

[Trait("Category", "Integration")]
[Collection(PostgreSqlFixtureDefinition.Name)]
public class ConnectionCheckerIntegrationTests(PostgreSqlFixture fixture)
{
    private readonly ConnectionChecker _checker = new();

    [Fact]
    public async Task Check_ForASuperuser_ReportsCreateDbAndOwnership()
    {
        var database = await fixture.CreateEmptyDatabaseAsync();

        var result = await _checker.CheckAsync(database.ConnectionString, TestContext.Current.CancellationToken);

        Assert.True(result.Connected);
        Assert.NotNull(result.ServerVersion);
        Assert.True(result.CanCreateDatabase);
        Assert.True(result.OwnsDatabase);
    }

    [Fact]
    public async Task Check_ForTheOwningRoleWithCreateDb_ReportsBoth()
    {
        var database = await fixture.CreateRestrictedRoleDatabaseAsync();

        var result = await _checker.CheckAsync(database.ConnectionString, TestContext.Current.CancellationToken);

        Assert.True(result.CanCreateDatabase);
        Assert.True(result.OwnsDatabase);
    }

    [Fact]
    public async Task Check_ForARoleWithoutCreateDb_ReportsItAndExplainsIt()
    {
        var database = await fixture.CreateRestrictedRoleDatabaseAsync(createDb: false);

        var result = await _checker.CheckAsync(database.ConnectionString, TestContext.Current.CancellationToken);

        Assert.True(result.Connected);
        Assert.False(result.CanCreateDatabase);
        Assert.True(result.OwnsDatabase);
    }

    [Fact]
    public async Task Check_ForARoleThatDoesNotOwnTheDatabase_ReportsIt()
    {
        var owned = await fixture.CreateRestrictedRoleDatabaseAsync();
        var other = await fixture.CreateEmptyDatabaseAsync();
        var asRole = new NpgsqlConnectionStringBuilder(owned.ConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(other.ConnectionString).Database,
        };

        var result = await _checker.CheckAsync(asRole.ConnectionString, TestContext.Current.CancellationToken);

        Assert.True(result.Connected);
        Assert.False(result.OwnsDatabase);
    }

    [Fact]
    public async Task Check_DetectsAnEmptyDatabase()
    {
        var database = await fixture.CreateEmptyDatabaseAsync();

        var result = await _checker.CheckAsync(database.ConnectionString, TestContext.Current.CancellationToken);

        Assert.Equal(TargetState.Empty, result.TargetState);
    }

    [Fact]
    public async Task Check_DetectsTheJellyfinSchema()
    {
        var database = await fixture.CreateDatabaseAsync();

        var result = await _checker.CheckAsync(database.ConnectionString, TestContext.Current.CancellationToken);

        Assert.Equal(TargetState.JellyfinSchema, result.TargetState);
    }

    [Fact]
    public async Task Check_DetectsForeignTables()
    {
        var database = await fixture.CreateEmptyDatabaseAsync();
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE something_else (id int)";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var result = await _checker.CheckAsync(database.ConnectionString, TestContext.Current.CancellationToken);

        Assert.Equal(TargetState.Other, result.TargetState);
    }

    [Fact]
    public async Task Check_TreatsAnotherApplicationsInitialCreateAsForeign()
    {
        var database = await fixture.CreateEmptyDatabaseAsync();
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE "__EFMigrationsHistory" ("MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
                INSERT INTO "__EFMigrationsHistory" VALUES ('20200101000000_InitialCreate', '8.0.0');
                CREATE TABLE "Orders" (id int);
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var result = await _checker.CheckAsync(database.ConnectionString, TestContext.Current.CancellationToken);

        Assert.Equal(TargetState.Other, result.TargetState);
    }

    [Fact]
    public async Task Check_WithAWrongPassword_ExplainsWithoutEchoingIt()
    {
        var database = await fixture.CreateRestrictedRoleDatabaseAsync();
        var wrong = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Password = "definitely-wrong-pw" }.ConnectionString;

        var result = await _checker.CheckAsync(wrong, TestContext.Current.CancellationToken);

        Assert.False(result.Connected);
        var blocker = Assert.Single(result.Blockers);
        Assert.Contains("password", blocker, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("definitely-wrong-pw", blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_WithAMissingDatabase_SaysItDoesNotExist()
    {
        var database = fixture.GetNotYetCreatedDatabase();

        var result = await _checker.CheckAsync(database.ConnectionString, TestContext.Current.CancellationToken);

        Assert.False(result.Connected);
        Assert.Contains("does not exist", Assert.Single(result.Blockers), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Check_WithAnUnreachableHost_TimesOutWithAClearMessage()
    {
        var unreachable = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 1, Username = "x", Password = "y" }.ConnectionString;

        var result = await _checker.CheckAsync(unreachable, TestContext.Current.CancellationToken);

        Assert.False(result.Connected);
        Assert.Contains("127.0.0.1", Assert.Single(result.Blockers), StringComparison.Ordinal);
    }
}
