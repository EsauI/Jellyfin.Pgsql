using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Pgsql.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

/// <summary>
/// Backup and restore against a real server. The plugin must work without any PostgreSQL client tool.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PostgreSqlFixtureDefinition.Name)]
public class PostgreSqlBackupIntegrationTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Backup_CreatesATemplateCloneWithTheSameData()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await AddActivityAsync(database, "original");

        var key = await database.Provider.MigrationBackupFast(token);

        var backupName = $"{database.DatabaseName}_backup_{key}";
        Assert.Equal([backupName], await fixture.ListDatabasesAsync(database.DatabaseName + "_backup_"));
        await using (var backup = CreateContextFor(database, backupName))
        {
            Assert.Equal(["original"], await backup.ActivityLogs.Select(a => a.Name).ToListAsync(token));
        }
    }

    [Fact]
    public async Task BackupThenRestore_WorksWithNoPostgreSqlClientToolsOnPath()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await AddActivityAsync(database, "original");
        var emptyBin = Directory.CreateTempSubdirectory("jellyfin-pgsql-nopath-").FullName;
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        string key;
        try
        {
            Environment.SetEnvironmentVariable("PATH", emptyBin);
            key = await database.Provider.MigrationBackupFast(token);
            await database.Provider.RestoreBackupFast(key, token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(emptyBin);
        }

        Assert.NotEqual(PostgreSqlBackupService.NoBackupKey, key);
        await using var verify = database.CreateContext();
        Assert.Equal(["original"], await verify.ActivityLogs.Select(a => a.Name).ToListAsync(token));
    }

    [Fact]
    public async Task BackupThenRestore_BringsTheOriginalDataBack_KeepsTheBackup_AndKeepsTheOldDatabase()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await AddActivityAsync(database, "original");

        var key = await database.Provider.MigrationBackupFast(token);
        await AddActivityAsync(database, "added after the backup");
        await database.Provider.RestoreBackupFast(key, token);

        await using (var verify = database.CreateContext())
        {
            Assert.Equal(["original"], await verify.ActivityLogs.Select(a => a.Name).ToListAsync(token));

            // The sequence came back with the data: a new row must not collide with the restored one.
            verify.ActivityLogs.Add(new ActivityLog("after restore", "test", Guid.NewGuid()));
            await verify.SaveChangesAsync(token);
        }

        var backupName = $"{database.DatabaseName}_backup_{key}";
        Assert.Equal([backupName], await fixture.ListDatabasesAsync(database.DatabaseName + "_backup_"));
        await using (var backup = CreateContextFor(database, backupName))
        {
            Assert.Equal(["original"], await backup.ActivityLogs.Select(a => a.Name).ToListAsync(token));
        }

        var kept = Assert.Single(await fixture.ListDatabasesAsync(database.DatabaseName + "_failed_"));
        await using var old = CreateContextFor(database, kept);
        Assert.Equal(["added after the backup", "original"], (await old.ActivityLogs.Select(a => a.Name).ToListAsync(token)).Order().ToArray());
    }

    [Fact]
    public async Task Backup_OfAnEmptyDatabase_SucceedsAndRestoreAndDeleteAreNoOps()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateEmptyDatabaseAsync();

        var key = await database.Provider.MigrationBackupFast(token);
        await database.Provider.RestoreBackupFast(key, token);
        await database.Provider.DeleteBackup(key);

        Assert.Equal(PostgreSqlBackupService.NoBackupKey, key);
        Assert.Empty(await fixture.ListDatabasesAsync(database.DatabaseName + "_"));
    }

    [Fact]
    public async Task Backup_OfADatabaseThatDoesNotExistYet_SucceedsAndRestoreAndDeleteAreNoOps()
    {
        var token = TestContext.Current.CancellationToken;
        var database = fixture.GetNotYetCreatedDatabase();

        var key = await database.Provider.MigrationBackupFast(token);
        await database.Provider.RestoreBackupFast(key, token);
        await database.Provider.DeleteBackup(key);

        Assert.Equal(PostgreSqlBackupService.NoBackupKey, key);
        Assert.Empty(await fixture.ListDatabasesAsync(database.DatabaseName));
    }

    [Fact]
    public async Task Backup_WhileAnotherSessionIsConnected_ThrowsAClearErrorAndDoesNotTerminateTheSession()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await using var other = await fixture.OpenAdminConnectionAsync(database.DatabaseName);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => database.Provider.MigrationBackupFast(token));

        Assert.Contains(database.DatabaseName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("another client", exception.Message, StringComparison.Ordinal);
        await using var command = other.CreateCommand();
        command.CommandText = "SELECT 1";
        Assert.Equal(1, await command.ExecuteScalarAsync(token));
        Assert.Empty(await fixture.ListDatabasesAsync(database.DatabaseName + "_backup_"));
    }

    [Theory]
    [InlineData("20200101000000")]
    [InlineData("20200101000000_3")]
    public async Task Restore_OfABackupThatDoesNotExist_ThrowsAndLeavesTheDatabaseIntact(string key)
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await AddActivityAsync(database, "keep me");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => database.Provider.RestoreBackupFast(key, token));

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
        await using var verify = database.CreateContext();
        Assert.Equal(["keep me"], await verify.ActivityLogs.Select(a => a.Name).ToListAsync(token));
        Assert.Empty(await fixture.ListDatabasesAsync(database.DatabaseName + "_"));
    }

    [Fact]
    public async Task Delete_DropsOnlyTheBackupDatabase_AndAnUnknownKeyDoesNotThrow()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await AddActivityAsync(database, "keep me");
        var key = await database.Provider.MigrationBackupFast(token);
        Assert.Single(await fixture.ListDatabasesAsync(database.DatabaseName + "_backup_"));

        await database.Provider.DeleteBackup(key);
        await database.Provider.DeleteBackup(key);
        await database.Provider.DeleteBackup("20200101000000");

        Assert.Empty(await fixture.ListDatabasesAsync(database.DatabaseName + "_backup_"));
        Assert.Single(await fixture.ListDatabasesAsync(database.DatabaseName));
        await using var verify = database.CreateContext();
        Assert.Equal(["keep me"], await verify.ActivityLogs.Select(a => a.Name).ToListAsync(token));
    }

    [Theory]
    [InlineData("Jf Mixed-Case \"Quoted\" db")]
    [InlineData("jf_long_xxxxxxxxxxxxxxxxxxxx")]
    public async Task BackupThenRestore_RoundTripsUnusualAndLongDatabaseNames(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        var token = TestContext.Current.CancellationToken;

        // The long stem plus a GUID makes a 60 byte name: the derived names need shortening to fit 63 bytes.
        var database = await fixture.CreateDatabaseAsync(stem + (stem.Length > 20 ? string.Empty : "_") + Guid.NewGuid().ToString("N"));
        await AddActivityAsync(database, "original");
        var before = await fixture.ListDatabasesAsync(string.Empty);

        var key = await database.Provider.MigrationBackupFast(token);
        await AddActivityAsync(database, "added after the backup");
        await database.Provider.RestoreBackupFast(key, token);

        await using var verify = database.CreateContext();
        Assert.Equal(["original"], await verify.ActivityLogs.Select(a => a.Name).ToListAsync(token));

        // Exactly the backup and the replaced database are new; none of the derived names was truncated into a collision.
        var created = (await fixture.ListDatabasesAsync(string.Empty)).Except(before).ToList();
        Assert.Equal(2, created.Count);
        Assert.All(created, created => Assert.True(Encoding.UTF8.GetByteCount(created) <= 63));
    }

    [Fact]
    public async Task BackupThenRestore_SucceedsForANonSuperuserRoleThatOwnsTheDatabaseAndHasCreateDb()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateRestrictedRoleDatabaseAsync();
        await AddActivityAsync(database, "original");

        var key = await database.Provider.MigrationBackupFast(token);
        await AddActivityAsync(database, "added after the backup");
        await database.Provider.RestoreBackupFast(key, token);
        await database.Provider.DeleteBackup(key);

        await using var verify = database.CreateContext();
        Assert.Equal(["original"], await verify.ActivityLogs.Select(a => a.Name).ToListAsync(token));
        Assert.Empty(await fixture.ListDatabasesAsync(database.DatabaseName + "_backup_"));
    }

    [Fact]
    public async Task Backup_WithARoleThatCannotCreateDatabases_ThrowsNamingTheRequirement()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateRestrictedRoleDatabaseAsync(createDb: false);
        await AddActivityAsync(database, "original");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => database.Provider.MigrationBackupFast(token));

        Assert.Contains("CREATEDB", exception.Message, StringComparison.Ordinal);
        Assert.Contains("owner", exception.Message, StringComparison.Ordinal);
    }

    private static async Task AddActivityAsync(TestDatabase database, string name)
    {
        await using var context = database.CreateContext();
        context.ActivityLogs.Add(new ActivityLog(name, "test", Guid.NewGuid()));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static JellyfinDbContext CreateContextFor(TestDatabase database, string databaseName)
        => new TestDatabase(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = databaseName }.ConnectionString).CreateContext();
}
