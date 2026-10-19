using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

/// <summary>
/// SQLite's LIKE ignores ASCII case and Jellyfin relies on it (OriginalTitle, activity log search); the provider must keep that.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PostgreSqlFixtureDefinition.Name)]
public class LikeTranslationTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Like_OnAMixedCaseBaseItemColumn_MatchesRegardlessOfCase()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await using (var seed = database.CreateContext())
        {
            seed.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", Name = "The Matrix", CleanName = "the matrix", OriginalTitle = "The Matrix" });
            await seed.SaveChangesAsync(token);
        }

        await using var context = database.CreateContext();
        var lower = await context.BaseItems.CountAsync(item => EF.Functions.Like(item.OriginalTitle!, "%matrix%"), token);
        var upper = await context.BaseItems.CountAsync(item => EF.Functions.Like(item.OriginalTitle!, "THE MATRIX"), token);
        var miss = await context.BaseItems.CountAsync(item => EF.Functions.Like(item.OriginalTitle!, "%reloaded%"), token);

        Assert.Equal(1, lower);
        Assert.Equal(1, upper);
        Assert.Equal(0, miss);
    }

    [Fact]
    public async Task Like_OnTheActivityLogName_MatchesRegardlessOfCase()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await using (var seed = database.CreateContext())
        {
            seed.ActivityLogs.Add(new ActivityLog("User Logged In", "Authentication", Guid.NewGuid()) { DateCreated = DateTime.UtcNow });
            await seed.SaveChangesAsync(token);
        }

        await using var context = database.CreateContext();
        var matches = await context.ActivityLogs.CountAsync(log => EF.Functions.Like(log.Name, "%logged%"), token);

        Assert.Equal(1, matches);
    }

    [Fact]
    public async Task Like_WithAnEscapeCharacter_MatchesRegardlessOfCaseAndTreatsTheEscapedWildcardLiterally()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await using (var seed = database.CreateContext())
        {
            seed.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", Name = "a", CleanName = "a", OriginalTitle = "100% Pure" });
            seed.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", Name = "b", CleanName = "b", OriginalTitle = "100 Percent" });
            await seed.SaveChangesAsync(token);
        }

        await using var context = database.CreateContext();
        var escaped = await context.BaseItems.CountAsync(item => EF.Functions.Like(item.OriginalTitle!, "100\\% pure", "\\"), token);
        var unescaped = await context.BaseItems.CountAsync(item => EF.Functions.Like(item.OriginalTitle!, "100% p%", "\\"), token);

        Assert.Equal(1, escaped);
        Assert.Equal(2, unescaped);
    }

    [Fact]
    public async Task Like_GeneratesILike()
    {
        var database = await fixture.CreateDatabaseAsync();
        await using var context = database.CreateContext();

        var plain = context.BaseItems.Where(item => EF.Functions.Like(item.OriginalTitle!, "%matrix%")).ToQueryString();
        var escaped = context.BaseItems.Where(item => EF.Functions.Like(item.OriginalTitle!, "100\\%", "\\")).ToQueryString();

        Assert.Contains(" ILIKE ", plain, StringComparison.Ordinal);
        Assert.Contains(" ILIKE ", escaped, StringComparison.Ordinal);
        Assert.Contains("ESCAPE", escaped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Contains_StillTranslatesToTheOrdinaryCaseSensitiveForm()
    {
        var database = await fixture.CreateDatabaseAsync();
        await using var context = database.CreateContext();

        var sql = context.BaseItems.Where(item => item.CleanName!.Contains("matrix")).ToQueryString();

        Assert.DoesNotContain("ILIKE", sql, StringComparison.Ordinal);
    }
}
