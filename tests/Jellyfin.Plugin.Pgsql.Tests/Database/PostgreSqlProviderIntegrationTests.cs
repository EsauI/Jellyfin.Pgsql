using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

[Trait("Category", "Integration")]
[Collection(PostgreSqlFixtureDefinition.Name)]
public class PostgreSqlProviderIntegrationTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task RefreshStatistics_RunsAgainstRealServer()
    {
        var database = await fixture.CreateDatabaseAsync();

        await database.Provider.RefreshStatistics(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunScheduledOptimisation_RunsOutsideATransaction()
    {
        var database = await fixture.CreateDatabaseAsync();

        // VACUUM fails with "cannot run inside a transaction block" if it is wrapped in one.
        await database.Provider.RunScheduledOptimisation(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PurgeDatabase_EmptiesNamedTablesAndResetsIdentities()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await using (var seed = database.CreateContext())
        {
            seed.ActivityLogs.Add(new ActivityLog("first", "test", Guid.NewGuid()));
            seed.ActivityLogs.Add(new ActivityLog("second", "test", Guid.NewGuid()));
            await seed.SaveChangesAsync(token);
        }

        await using (var purge = database.CreateContext())
        {
            await database.Provider.PurgeDatabase(purge, ["ActivityLogs"]);
        }

        await using var verify = database.CreateContext();
        Assert.Empty(await verify.ActivityLogs.ToListAsync(token));
        verify.ActivityLogs.Add(new ActivityLog("after", "test", Guid.NewGuid()));
        await verify.SaveChangesAsync(token);
        Assert.Equal(1, (await verify.ActivityLogs.SingleAsync(token)).Id);
    }

    [Fact]
    public async Task PurgeDatabase_WithoutTableNames_EmptiesEveryTable()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await using (var seed = database.CreateContext())
        {
            seed.ActivityLogs.Add(new ActivityLog("entry", "test", Guid.NewGuid()));
            await seed.SaveChangesAsync(token);
        }

        await using (var purge = database.CreateContext())
        {
            await database.Provider.PurgeDatabase(purge, null);
        }

        await using var verify = database.CreateContext();
        Assert.False(await verify.ActivityLogs.AnyAsync(token));
    }

    [Fact]
    public async Task PurgeDatabase_WithServerSchemaQualifiedNames_EmptiesTables()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await using (var seed = database.CreateContext())
        {
            seed.ActivityLogs.Add(new ActivityLog("entry", "test", Guid.NewGuid()));
            await seed.SaveChangesAsync(token);
        }

        await using (var purge = database.CreateContext())
        {
            // Same names Jellyfin's BackupService passes before a restore.
            var tableNames = purge.Model.GetEntityTypes()
                .Select(entity => entity.GetSchemaQualifiedTableName())
                .OfType<string>()
                .Distinct()
                .ToList();
            await database.Provider.PurgeDatabase(purge, tableNames);
        }

        await using var verify = database.CreateContext();
        Assert.False(await verify.ActivityLogs.AnyAsync(token));
    }

    [Fact]
    public async Task PurgeDatabase_ThenRestoreRowsWithIds_NextInsertGetsFreshId()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        var backup = await SeedActivityLogsAsync(database, 3, token);

        await RestoreLikeBackupServiceAsync(database, backup, token);

        await AssertNextInsertSucceedsAsync(database, expectedCount: 4, token);
    }

    [Fact]
    public async Task PurgeDatabase_ThenRestoreIntoAnotherDatabase_NextInsertGetsFreshId()
    {
        var token = TestContext.Current.CancellationToken;
        var source = await fixture.CreateDatabaseAsync();
        var backup = await SeedActivityLogsAsync(source, 3, token);
        var target = await fixture.CreateDatabaseAsync();

        await RestoreLikeBackupServiceAsync(target, backup, token);

        await AssertNextInsertSucceedsAsync(target, expectedCount: 4, token);
    }

    [Theory]
    [InlineData("NoSuchTable")]
    [InlineData("ActivityLogs\"; DROP TABLE \"Users\"; --")]
    public async Task PurgeDatabase_UnknownTableName_ThrowsAndKeepsData(string tableName)
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await using (var seed = database.CreateContext())
        {
            seed.ActivityLogs.Add(new ActivityLog("entry", "test", Guid.NewGuid()));
            await seed.SaveChangesAsync(token);
        }

        await using (var purge = database.CreateContext())
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => database.Provider.PurgeDatabase(purge, ["ActivityLogs", tableName]));
        }

        await using var verify = database.CreateContext();
        Assert.Single(await verify.ActivityLogs.ToListAsync(token));
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public async Task DateTime_RoundTripsAsTheSameUtcInstant(DateTimeKind kind)
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        var written = DateTime.SpecifyKind(new DateTime(2026, 3, 1, 12, 30, 15, 123), kind);
        var expectedUtc = kind == DateTimeKind.Local ? written.ToUniversalTime() : DateTime.SpecifyKind(written, DateTimeKind.Utc);

        await using (var write = database.CreateContext())
        {
            var log = new ActivityLog("entry", "test", Guid.NewGuid()) { DateCreated = written };
            write.ActivityLogs.Add(log);
            await write.SaveChangesAsync(token);
        }

        await using var read = database.CreateContext();
        var stored = (await read.ActivityLogs.AsNoTracking().ToListAsync(token)).Single().DateCreated;
        Assert.Equal(DateTimeKind.Utc, stored.Kind);
        Assert.Equal(expectedUtc, stored);
    }

    private static async Task<ActivityLog[]> SeedActivityLogsAsync(TestDatabase database, int count, CancellationToken token)
    {
        await using var seed = database.CreateContext();
        for (var i = 0; i < count; i++)
        {
            seed.ActivityLogs.Add(new ActivityLog("entry" + i, "test", Guid.NewGuid()));
        }

        await seed.SaveChangesAsync(token);
        return await seed.ActivityLogs.AsNoTracking().ToArrayAsync(token);
    }

    // Mirrors Jellyfin 12.2 BackupService.RestoreBackupAsync: purge with schema-qualified names, add rows with ids, save once.
    private static async Task RestoreLikeBackupServiceAsync(TestDatabase database, ActivityLog[] backup, CancellationToken token)
    {
        await using var restore = database.CreateContext();
        var tableNames = restore.Model.GetEntityTypes()
            .Select(entity => entity.GetSchemaQualifiedTableName())
            .OfType<string>()
            .Distinct()
            .ToList();
        await database.Provider.PurgeDatabase(restore, tableNames);
        foreach (var row in backup)
        {
            restore.Add(row);
        }

        await restore.SaveChangesAsync(token);
    }

    private static async Task AssertNextInsertSucceedsAsync(TestDatabase database, int expectedCount, CancellationToken token)
    {
        await using var after = database.CreateContext();
        after.ActivityLogs.Add(new ActivityLog("new", "test", Guid.NewGuid()));
        await after.SaveChangesAsync(token);
        Assert.Equal(expectedCount, await after.ActivityLogs.CountAsync(token));
    }
}
