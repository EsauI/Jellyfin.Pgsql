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
/// Data and checks shared by the restore tests.
/// </summary>
internal static class RestoreScenario
{
    internal static async Task<(List<object> Backup, List<HomeSection> HomeSections)> SeedAsync(TestDatabase database, CancellationToken token)
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
        backup.AddRange(await seed.BaseItems.AsNoTracking().ToListAsync(token));
        backup.AddRange(await seed.Users.AsNoTracking().ToListAsync(token));
        backup.AddRange(await seed.DisplayPreferences.AsNoTracking().ToListAsync(token));
        var homeSections = await seed.Set<HomeSection>().AsNoTracking().OrderBy(section => section.Id).ToListAsync(token);
        return (backup, homeSections);
    }

    internal static async Task AssertNextHomeSectionGetsFreshIdAsync(TestDatabase database, int highestId, CancellationToken token)
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

    /// <summary>
    /// Reads the row count and an md5 of the content of every table of the model.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="token">The cancellation token.</param>
    /// <returns>One line per table, ordered by table name.</returns>
    internal static async Task<IReadOnlyList<string>> FingerprintAsync(TestDatabase database, CancellationToken token)
    {
        await using var context = database.CreateContext();
        var tables = context.Model.GetEntityTypes().Select(entity => entity.GetTableName()!).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToList();
        var connection = new NpgsqlConnection(database.ConnectionString);
        await using (connection)
        {
            await connection.OpenAsync(token);
            var lines = new List<string>();
            foreach (var table in tables)
            {
                await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Table names come from the EF model.
                command.CommandText = $"SELECT count(*) || ' ' || coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '-') FROM \"{table}\" t";
#pragma warning restore CA2100
                lines.Add(table + ": " + await command.ExecuteScalarAsync(token));
            }

            return lines;
        }
    }
}
