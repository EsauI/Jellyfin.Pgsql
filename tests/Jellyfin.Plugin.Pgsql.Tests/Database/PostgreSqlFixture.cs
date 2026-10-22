using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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
    /// Creates a database with the Jellyfin schema applied through the plugin's migrations.
    /// </summary>
    /// <returns>The new database.</returns>
    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var database = await CreateEmptyDatabaseAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        return database;
    }

    /// <summary>
    /// Returns a provider wired to a database name that does not exist on the server yet.
    /// </summary>
    /// <returns>A database the test has to create itself.</returns>
    public TestDatabase GetNotYetCreatedDatabase()
    {
        var name = "jf_" + Guid.NewGuid().ToString("N");
        return new TestDatabase(new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString);
    }

    /// <summary>
    /// Creates a database without any schema and returns a provider wired to it.
    /// </summary>
    /// <returns>The new database.</returns>
    public async Task<TestDatabase> CreateEmptyDatabaseAsync()
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
        return new TestDatabase(connectionString);
    }
}
