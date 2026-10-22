using System;
using System.Threading.Tasks;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

/// <summary>
/// Starts one PostgreSQL server for all integration tests and hands out an isolated database per test.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// Creates an empty database with the Jellyfin schema and returns a provider wired to it.
    /// </summary>
    /// <returns>The new database.</returns>
    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = "jf_" + Guid.NewGuid().ToString("N");
        var admin = new NpgsqlConnection(_container.GetConnectionString());
        await using (admin)
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
#pragma warning disable CA2100 // The name is a generated GUID, not user input.
            command.CommandText = $"CREATE DATABASE \"{name}\"";
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
        var database = new TestDatabase(connectionString);
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        return database;
    }
}
