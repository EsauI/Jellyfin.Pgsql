using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

[Trait("Category", "Integration")]
[Collection(PostgreSqlFixtureDefinition.Name)]
public class MigrationTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Migrations_ApplyToAnEmptyDatabase()
    {
        var database = await fixture.CreateEmptyDatabaseAsync();
        await using var context = database.CreateContext();

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Contains(
            await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken),
            id => id.EndsWith("_InitialCreate", StringComparison.Ordinal));
        Assert.True(await context.BaseItems.AnyAsync(item => item.Type == "PLACEHOLDER", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Migrations_AppliedTwice_AreANoOp()
    {
        var database = await fixture.CreateEmptyDatabaseAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var applied = await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(applied, await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await context.BaseItems.CountAsync(item => item.Type == "PLACEHOLDER", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Migrations_ReplayTheServersFreshInstallSequence()
    {
        // Jellyfin never calls MigrateAsync. On a fresh install it records its code migrations in the history table
        // (JellyfinMigrationService.cs:116-128), then applies the pending EF migrations one by one through IMigrator.
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateEmptyDatabaseAsync();
        await using var context = database.CreateContext();
        var history = context.GetService<IHistoryRepository>();
        await history.CreateIfNotExistsAsync(token);
        foreach (var id in Jellyfin122CodeMigrations.SeededIds)
        {
            await context.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "12.2.0")), token);
        }

        var pending = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order(StringComparer.Ordinal).ToArray();
        foreach (var migration in pending)
        {
            await context.GetService<IMigrator>().MigrateAsync(migration, token);
        }

        var applied = (await history.GetAppliedMigrationsAsync(token)).Select(row => row.MigrationId).ToArray();
        Assert.All(Jellyfin122CodeMigrations.SeededIds, id => Assert.Contains(id, applied));
        Assert.Equal(Jellyfin122CodeMigrations.SeededIds.Count + pending.Length, applied.Length);
        Assert.Equal(pending, applied.Where(id => id.EndsWith("_InitialCreate", StringComparison.Ordinal)).ToArray());
        Assert.All(Jellyfin122CodeMigrations.SeededIds, id => Assert.True(string.CompareOrdinal(id, pending[0]) < 0));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(token));
        Assert.True(await context.BaseItems.AnyAsync(item => item.Type == "PLACEHOLDER", token));
    }

    [Fact]
    public async Task Database_CreatedByTheServerBeforeMigrating_ThenAcceptsTheMigrations()
    {
        // Jellyfin creates a missing database itself (JellyfinMigrationService.cs:110) before any migration runs.
        var token = TestContext.Current.CancellationToken;
        var database = fixture.GetNotYetCreatedDatabase();
        await using var context = database.CreateContext();
        var creator = context.GetService<IDatabaseCreator>() as IRelationalDatabaseCreator;
        Assert.NotNull(creator);
        Assert.False(await creator.ExistsAsync(token));

        await creator.CreateAsync(token);
        await context.Database.MigrateAsync(token);

        Assert.True(await creator.ExistsAsync(token));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(token));
    }

    [Fact]
    public async Task Model_HasNoPendingChangesComparedToTheSnapshot()
    {
        var database = await fixture.CreateEmptyDatabaseAsync();
        await using var context = database.CreateContext();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("IX_BaseItems_OwnerId", "(\"OwnerId\" IS NOT NULL)")]
    [InlineData("IX_BaseItems_ExtraType_OwnerId", "(\"ExtraType\" IS NOT NULL)")]
    [InlineData("IX_BaseItems_PrimaryVersionId", "(\"PrimaryVersionId\" IS NOT NULL)")]
    [InlineData("IX_BaseItems_TopParentId_Type_IsVirtualItem", "((\"PrimaryVersionId\" IS NULL) AND ((\"OwnerId\" IS NULL) OR (\"ExtraType\" IS NOT NULL)))")]
    public async Task PartialIndexes_ExistWithTheExactWhereClause(string indexName, string expectedPredicate)
    {
        var database = await fixture.CreateDatabaseAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT indexdef FROM pg_indexes WHERE tablename = 'BaseItems' AND indexname = @name";
        command.Parameters.AddWithValue("name", indexName);

        var definition = (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(definition);
        Assert.EndsWith(" WHERE " + expectedPredicate, definition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialIndexes_AreTheOnlyFilteredIndexesInTheModel()
    {
        var database = await fixture.CreateDatabaseAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND indexdef LIKE '% WHERE %' ORDER BY indexname";
        var filtered = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                filtered.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            ["IX_BaseItems_ExtraType_OwnerId", "IX_BaseItems_OwnerId", "IX_BaseItems_PrimaryVersionId", "IX_BaseItems_TopParentId_Type_IsVirtualItem"],
            filtered);
    }

    [Fact]
    public async Task NameSearch_ThroughTheLowerCasedCleanName_FindsMixedCaseTitles()
    {
        // Jellyfin lower-cases the search term (GetCleanValue) and matches it against the already-clean CleanName, so the
        // search does not depend on any database collation (BaseItemRepository.TranslateQuery.cs:262-273).
        var token = TestContext.Current.CancellationToken;
        var database = await fixture.CreateDatabaseAsync();
        await using (var seed = database.CreateContext())
        {
            seed.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", Name = "The Matrix", CleanName = "the matrix", OriginalTitle = "The Matrix" });
            await seed.SaveChangesAsync(token);
        }

        await using var context = database.CreateContext();
        var contains = await context.BaseItems.Where(item => item.CleanName!.Contains("matrix")).CountAsync(token);
        var like = await context.BaseItems.Where(item => EF.Functions.Like(item.CleanName!, "%matrix%")).CountAsync(token);

        Assert.Equal(1, contains);
        Assert.Equal(1, like);
    }
}
