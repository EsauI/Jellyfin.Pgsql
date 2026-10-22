using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

/// <summary>
/// Restores like Jellyfin's BackupService, whose table list omits <c>HomeSection</c> although that table
/// references <c>DisplayPreferences</c>. SQLite keeps such rows; so must this provider.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PostgreSqlFixtureDefinition.Name)]
public class PurgeRestoreIntegrationTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Restore_KeepsHomeSectionsOfRestoredDisplayPreferences()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        var (backup, before) = await SeedAsync(database, token);

        await BackupServiceRestore.RestoreAsync(database, backup, token);

        await using var verify = database.CreateContext();
        var after = await verify.Set<HomeSection>().AsNoTracking().OrderBy(section => section.Id).ToListAsync(token);
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Id, after[i].Id);
            Assert.Equal(before[i].DisplayPreferencesId, after[i].DisplayPreferencesId);
            Assert.Equal(before[i].Order, after[i].Order);
            Assert.Equal(before[i].Type, after[i].Type);
        }
    }

    [Fact]
    public async Task Restore_SkipsSnapshotRowsWhoseParentWasNotRestored()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        var (backup, _) = await SeedAsync(database, token);

        // The archive holds the users but no display preferences: the home sections are orphans.
        await BackupServiceRestore.RestoreAsync(database, backup.OfType<User>(), token);

        await using var verify = database.CreateContext();
        Assert.Single(await verify.Users.ToListAsync(token));
        Assert.Empty(await verify.Set<HomeSection>().ToListAsync(token));
    }

    [Fact]
    public async Task Restore_ReinsertedHomeSectionIdsAreCountedBySequence()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        var (backup, before) = await SeedAsync(database, token);

        await BackupServiceRestore.RestoreAsync(database, backup, token);

        await AssertNextHomeSectionGetsFreshIdAsync(database, before.Max(section => section.Id), token);
    }

    [Fact]
    public async Task Restore_UnrelatedSaveFromAnotherContext_DoesNotConsumeThePendingRestore()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        var (backup, before) = await SeedAsync(database, token);

        await BackupServiceRestore.RestoreAsync(
            database,
            backup,
            token,
            async _ =>
            {
                await using var other = database.CreateContext();
                other.ActivityLogs.Add(new ActivityLog("unrelated", "test", Guid.NewGuid()));
                await other.SaveChangesAsync(token);
            });

        await using (var verify = database.CreateContext())
        {
            Assert.Equal(before.Count, await verify.Set<HomeSection>().CountAsync(token));
        }

        await AssertNextHomeSectionGetsFreshIdAsync(database, before.Max(section => section.Id), token);
    }

    [Fact]
    public async Task Restore_WhenReinsertFails_RaisesAndPerformsItOnTheNextSave()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        var (backup, before) = await SeedAsync(database, token);

        await using var restore = database.CreateContext();
        await database.Provider.PurgeDatabase(restore, BackupServiceRestore.TableNames(restore));
        foreach (var row in backup)
        {
            restore.Add(row);
        }

        // Make the follow-up statements fail once: the table the snapshot belongs to is temporarily gone.
        await restore.Database.ExecuteSqlRawAsync("ALTER TABLE \"HomeSection\" RENAME TO \"HomeSection_away\"", token);
        await Assert.ThrowsAsync<PostgresException>(() => restore.SaveChangesAsync(token));
        await restore.Database.ExecuteSqlRawAsync("ALTER TABLE \"HomeSection_away\" RENAME TO \"HomeSection\"", token);

        // The restore itself was committed; the retained state is applied by the next save of the same context.
        restore.ActivityLogs.Add(new ActivityLog("retry", "test", Guid.NewGuid()));
        await restore.SaveChangesAsync(token);

        await using var verify = database.CreateContext();
        Assert.Equal(before.Count, await verify.Set<HomeSection>().CountAsync(token));
        await AssertNextHomeSectionGetsFreshIdAsync(database, before.Max(section => section.Id), token);
    }

    [Fact]
    public async Task Purge_WithoutTableNames_StillEmptiesEveryTableIncludingHomeSection()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await SeedAsync(database, token);

        await using (var purge = database.CreateContext())
        {
            await database.Provider.PurgeDatabase(purge, null);
            await purge.SaveChangesAsync(token);
        }

        await using var verify = database.CreateContext();
        Assert.Empty(await verify.Users.ToListAsync(token));
        Assert.Empty(await verify.Set<HomeSection>().ToListAsync(token));
    }

    private static async Task<(List<object> Backup, List<HomeSection> HomeSections)> SeedAsync(TestDatabase database, CancellationToken token)
    {
        await using var seed = database.CreateContext();
        var user = new User("user", "auth", "reset");
        seed.Users.Add(user);
        await seed.SaveChangesAsync(token);

        var web = new DisplayPreferences(user.Id, Guid.NewGuid(), "web");
        web.HomeSections.Add(new HomeSection { Order = 0, Type = HomeSectionType.SmallLibraryTiles });
        web.HomeSections.Add(new HomeSection { Order = 1, Type = HomeSectionType.LatestMedia });
        var tv = new DisplayPreferences(user.Id, Guid.NewGuid(), "tv");
        tv.HomeSections.Add(new HomeSection { Order = 0, Type = HomeSectionType.Resume });
        seed.DisplayPreferences.AddRange(web, tv);
        await seed.SaveChangesAsync(token);

        // The archive holds the DbSet tables only, as untracked entities with their ids.
        var backup = new List<object>();
        backup.AddRange(await seed.Users.AsNoTracking().ToListAsync(token));
        backup.AddRange(await seed.DisplayPreferences.AsNoTracking().ToListAsync(token));
        var homeSections = await seed.Set<HomeSection>().AsNoTracking().OrderBy(section => section.Id).ToListAsync(token);
        return (backup, homeSections);
    }

    private static async Task AssertNextHomeSectionGetsFreshIdAsync(TestDatabase database, int highestId, CancellationToken token)
    {
        await using var next = database.CreateContext();
        var user = await next.Users.AsNoTracking().SingleAsync(token);
        var preferences = new DisplayPreferences(user.Id, Guid.NewGuid(), "next");
        var section = new HomeSection { Order = 0, Type = HomeSectionType.None };
        preferences.HomeSections.Add(section);
        next.DisplayPreferences.Add(preferences);
        await next.SaveChangesAsync(token);
        Assert.True(section.Id > highestId, $"expected an id above {highestId} but got {section.Id}");
    }
}
