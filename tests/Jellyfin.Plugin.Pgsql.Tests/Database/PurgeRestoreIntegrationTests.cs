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
        var (backup, before) = await RestoreScenario.SeedAsync(database, token);

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
        var (backup, _) = await RestoreScenario.SeedAsync(database, token);

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
        var (backup, before) = await RestoreScenario.SeedAsync(database, token);

        await BackupServiceRestore.RestoreAsync(database, backup, token);

        await RestoreScenario.AssertNextHomeSectionGetsFreshIdAsync(database, before.Max(section => section.Id), token);
    }

    [Fact]
    public async Task Restore_UnrelatedSaveFromAnotherContext_DoesNotConsumeThePendingRestore()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        var (backup, before) = await RestoreScenario.SeedAsync(database, token);

        await BackupServiceRestore.RestoreAsync(
            database,
            backup,
            token,
            async _ =>
            {
                // The purge holds its table locks until the restore has been saved, so the other context saves
                // nothing to the database; its save still raises the interceptor events.
                await using var other = database.CreateContext();
                await other.SaveChangesAsync(token);
            });

        await using (var verify = database.CreateContext())
        {
            Assert.Equal(before.Count, await verify.Set<HomeSection>().CountAsync(token));
        }

        await RestoreScenario.AssertNextHomeSectionGetsFreshIdAsync(database, before.Max(section => section.Id), token);
    }

    [Fact]
    public async Task Purge_WithoutTableNames_StillEmptiesEveryTableIncludingHomeSection()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await RestoreScenario.SeedAsync(database, token);

        await using (var purge = database.CreateContext())
        {
            await database.Provider.PurgeDatabase(purge, null);
            await purge.SaveChangesAsync(token);
        }

        await using var verify = database.CreateContext();
        Assert.Empty(await verify.Users.ToListAsync(token));
        Assert.Empty(await verify.Set<HomeSection>().ToListAsync(token));
    }
}
