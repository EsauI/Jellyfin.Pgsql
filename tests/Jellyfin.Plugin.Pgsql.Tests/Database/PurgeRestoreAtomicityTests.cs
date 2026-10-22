using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

/// <summary>
/// The purge, the restore save and the follow-up are one unit: the server never saves the restoring context
/// again, so a half-finished restore could not be repaired later.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PostgreSqlFixtureDefinition.Name)]
public class PurgeRestoreAtomicityTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Restore_WhenFollowUpFails_RollsBackToTheOriginalData()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        var (backup, _) = await RestoreScenario.SeedAsync(database, token);
        var original = await RestoreScenario.FingerprintAsync(database, token);

        await using (var restore = database.CreateContext())
        {
            await database.Provider.PurgeDatabase(restore, BackupServiceRestore.TableNames(restore));
            backup.ForEach(row => restore.Add(row));

            // The statements that put the cascaded rows back fail: their table is gone.
            await restore.Database.ExecuteSqlRawAsync("ALTER TABLE \"HomeSection\" RENAME TO \"HomeSection_away\"", token);
            await Assert.ThrowsAsync<PostgresException>(() => restore.SaveChangesAsync(token));
        }

        Assert.Equal(original, await RestoreScenario.FingerprintAsync(database, token));
    }

    [Fact]
    public async Task Restore_WhenTheSaveFails_RollsBackToTheOriginalData()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        var (backup, _) = await RestoreScenario.SeedAsync(database, token);
        var original = await RestoreScenario.FingerprintAsync(database, token);

        await using (var restore = database.CreateContext())
        {
            await database.Provider.PurgeDatabase(restore, BackupServiceRestore.TableNames(restore));
            backup.ForEach(row => restore.Add(row));

            // Violates the foreign key to Users, so the save itself fails after the purge.
            restore.DisplayPreferences.Add(new DisplayPreferences(Guid.NewGuid(), Guid.NewGuid(), "orphan"));
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => restore.SaveChangesAsync(token));
        }

        Assert.Equal(original, await RestoreScenario.FingerprintAsync(database, token));
    }

    [Fact]
    public async Task Restore_WhenItSucceeds_ReproducesTheBackupKeepsHomeSectionsAndResyncsSequences()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        var (backup, homeSections) = await RestoreScenario.SeedAsync(database, token);
        var original = await RestoreScenario.FingerprintAsync(database, token);

        await BackupServiceRestore.RestoreAsync(database, backup, token);

        Assert.Equal(original, await RestoreScenario.FingerprintAsync(database, token));
        await RestoreScenario.AssertNextHomeSectionGetsFreshIdAsync(database, homeSections.Max(section => section.Id), token);
    }

    [Fact]
    public async Task Restore_AfterTwoPurgesOnOneContext_StillKeepsHomeSections()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        var (backup, _) = await RestoreScenario.SeedAsync(database, token);
        var original = await RestoreScenario.FingerprintAsync(database, token);

        await using (var restore = database.CreateContext())
        {
            await database.Provider.PurgeDatabase(restore, BackupServiceRestore.TableNames(restore));
            await database.Provider.PurgeDatabase(restore, BackupServiceRestore.TableNames(restore));
            backup.ForEach(row => restore.Add(row));
            await restore.SaveChangesAsync(token);
            Assert.Null(restore.Database.CurrentTransaction);
        }

        Assert.Equal(original, await RestoreScenario.FingerprintAsync(database, token));
    }

    [Fact]
    public async Task Purge_WithoutTableNamesAfterAListedPurge_DropsTheHeldBackRows()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        var (backup, _) = await RestoreScenario.SeedAsync(database, token);

        await using (var restore = database.CreateContext())
        {
            await database.Provider.PurgeDatabase(restore, BackupServiceRestore.TableNames(restore));
            await database.Provider.PurgeDatabase(restore, null);
            backup.ForEach(row => restore.Add(row));
            await restore.SaveChangesAsync(token);
        }

        await using var verify = database.CreateContext();
        Assert.NotEmpty(await verify.DisplayPreferences.ToListAsync(token));
        Assert.Empty(await verify.Set<HomeSection>().ToListAsync(token));
    }

    [Fact]
    public async Task Purge_InsideACallersTransaction_LeavesCommitAndRollbackToTheCaller()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        var (backup, _) = await RestoreScenario.SeedAsync(database, token);
        var original = await RestoreScenario.FingerprintAsync(database, token);

        await using (var restore = database.CreateContext())
        {
            var transaction = await restore.Database.BeginTransactionAsync(token);
            await database.Provider.PurgeDatabase(restore, BackupServiceRestore.TableNames(restore));
            backup.ForEach(row => restore.Add(row));
            await restore.SaveChangesAsync(token);

            Assert.Same(transaction, restore.Database.CurrentTransaction);
            await transaction.RollbackAsync(token);
        }

        Assert.Equal(original, await RestoreScenario.FingerprintAsync(database, token));
    }

    [Fact]
    public async Task Purge_OnAPooledContextReturnedWithoutSaving_DoesNotLeakItsTransactionIntoTheNextLease()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await RestoreScenario.SeedAsync(database, token);
        var original = await RestoreScenario.FingerprintAsync(database, token);
        var factory = database.CreatePooledFactory();

        var first = await factory.CreateDbContextAsync(token);
        await database.Provider.PurgeDatabase(first, BackupServiceRestore.TableNames(first));
        await first.DisposeAsync();

        var second = await factory.CreateDbContextAsync(token);
        await using (second)
        {
            Assert.Same(first, second);
            Assert.Null(second.Database.CurrentTransaction);
            second.Database.SetCommandTimeout(TimeSpan.FromSeconds(10));

            // An ordinary save on the reused context must neither complete a restore nor block on the purge's locks.
            second.ActivityLogs.Add(new ActivityLog("after", "test", Guid.NewGuid()));
            await second.SaveChangesAsync(token);
        }

        var after = await RestoreScenario.FingerprintAsync(database, token);
        Assert.Equal(original.Where(line => !line.StartsWith("ActivityLogs:", StringComparison.Ordinal)), after.Where(line => !line.StartsWith("ActivityLogs:", StringComparison.Ordinal)));
    }

    [Fact]
    public void BackupServiceTableList_LeavesOutExactlyTheHomeSectionTable()
    {
        var database = new TestDatabase("Host=unused;Database=unused");
        using var context = database.CreateContext();
        var listed = BackupServiceRestore.TableNames(context)
            .Select(name => context.Model.GetEntityTypes().First(entity => entity.GetSchemaQualifiedTableName() == name))
            .Select(entity => new Jellyfin.Plugin.Pgsql.Database.TableRef(entity.GetSchema(), entity.GetTableName()!))
            .Distinct()
            .ToList();

        var cascaded = Jellyfin.Plugin.Pgsql.Database.CascadeResolver.FindCascaded(context.Model, listed);

        // A table that appears here is emptied by the restore's purge and re-inserted from a generic snapshot;
        // a new one needs its column types checked.
        Assert.Equal("HomeSection", Assert.Single(cascaded).Name);
    }
}
