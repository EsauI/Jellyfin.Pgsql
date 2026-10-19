using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
    public async Task Migrations_ApplyAfterTheServerSeededItsCodeMigrationHistory()
    {
        // On a fresh install Jellyfin creates the history table and records its code migrations before any EF migration runs
        // (JellyfinMigrationService.cs:116-128), then applies this plugin's migrations one by one through IMigrator.
        var database = await fixture.CreateEmptyDatabaseAsync();
        await using var context = database.CreateContext();
        var history = context.GetService<IHistoryRepository>();
        await history.CreateIfNotExistsAsync(TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            history.GetInsertScript(new HistoryRow("20250420230000_SeededCodeMigration", "12.2.0")),
            TestContext.Current.CancellationToken);
        var migration = context.GetService<IMigrationsAssembly>().Migrations.Single().Key;

        await context.GetService<IMigrator>().MigrateAsync(migration, TestContext.Current.CancellationToken);

        var applied = await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        Assert.Contains("20250420230000_SeededCodeMigration", applied);
        Assert.Contains(migration, applied);
    }

    [Fact]
    public async Task Model_HasNoPendingChangesComparedToTheSnapshot()
    {
        var database = await fixture.CreateEmptyDatabaseAsync();
        await using var context = database.CreateContext();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("IX_BaseItems_OwnerId", "OwnerId")]
    [InlineData("IX_BaseItems_ExtraType_OwnerId", "ExtraType")]
    [InlineData("IX_BaseItems_PrimaryVersionId", "PrimaryVersionId")]
    [InlineData("IX_BaseItems_TopParentId_Type_IsVirtualItem", "PrimaryVersionId")]
    public async Task PartialIndexes_ExistWithAWhereClause(string indexName, string filteredColumn)
    {
        var database = await fixture.CreateDatabaseAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT indexdef FROM pg_indexes WHERE tablename = 'BaseItems' AND indexname = @name";
        command.Parameters.AddWithValue("name", indexName);

        var definition = (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(definition);
        Assert.Contains(" WHERE ", definition, StringComparison.Ordinal);
        Assert.Contains($"\"{filteredColumn}\"", definition[definition.IndexOf(" WHERE ", StringComparison.Ordinal)..], StringComparison.Ordinal);
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
    public async Task NameSearch_ThroughCleanName_IsCaseInsensitiveUnderTheCCollation()
    {
        // Jellyfin lower-cases the search term (GetCleanValue) and matches it against the already-clean CleanName, so the
        // case-sensitive "C" collation does not change the result (BaseItemRepository.TranslateQuery.cs:262-273).
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
