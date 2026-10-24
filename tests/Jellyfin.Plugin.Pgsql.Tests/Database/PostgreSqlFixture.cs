using System;
using System.Collections.Generic;
using System.IO;
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
    /// Lists the names of all databases on the server that start with <paramref name="prefix"/>.
    /// </summary>
    /// <param name="prefix">The name prefix.</param>
    /// <returns>The matching database names.</returns>
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(string prefix)
    {
        var admin = new NpgsqlConnection(_container.GetConnectionString());
        await using (admin)
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = "SELECT datname FROM pg_database WHERE starts_with(datname, @prefix) ORDER BY datname";
            command.Parameters.AddWithValue("prefix", prefix);
            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }
    }

    /// <summary>
    /// Creates a database with the Jellyfin schema applied through the plugin's migrations.
    /// </summary>
    /// <returns>The new database.</returns>
    /// <param name="name">The database name, or <see langword="null"/> for a generated one.</param>
    public async Task<TestDatabase> CreateDatabaseAsync(string? name = null)
    {
        var database = await CreateEmptyDatabaseAsync(name);
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
    /// <param name="name">The database name, or <see langword="null"/> for a generated one.</param>
    public async Task<TestDatabase> CreateEmptyDatabaseAsync(string? name = null)
    {
        name ??= "jf_" + Guid.NewGuid().ToString("N");
        await ExecuteAsAdminAsync($"CREATE DATABASE {Quote(name)}");

        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
        return new TestDatabase(connectionString);
    }

    /// <summary>
    /// Creates a login role that is not a superuser and only has <c>CREATEDB</c>, together with an empty database it
    /// owns, and returns a provider wired to that database as that role.
    /// </summary>
    /// <param name="createDb">Whether the role gets the <c>CREATEDB</c> privilege.</param>
    /// <param name="password">The role's password.</param>
    /// <returns>The new database, reachable only as the restricted role.</returns>
    public async Task<TestDatabase> CreateRestrictedRoleDatabaseAsync(bool createDb = true, string password = "restricted")
    {
        ArgumentNullException.ThrowIfNull(password);
        var suffix = Guid.NewGuid().ToString("N");
        var role = "jf_role_" + suffix;
        var name = "jf_" + suffix;
        await ExecuteAsAdminAsync($"CREATE ROLE {Quote(role)} LOGIN PASSWORD '{password.Replace("'", "''", StringComparison.Ordinal)}' {(createDb ? "CREATEDB" : "NOCREATEDB")} NOSUPERUSER");
        await ExecuteAsAdminAsync($"CREATE DATABASE {Quote(name)} OWNER {Quote(role)}");

        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = name,
            Username = role,
            Password = password,
        }.ConnectionString;
        var database = new TestDatabase(connectionString);
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        return database;
    }

    /// <summary>
    /// Returns an unpooled connection to the named database as the server's superuser.
    /// </summary>
    /// <param name="database">The database to connect to.</param>
    /// <returns>An open connection the caller must dispose.</returns>
    public async Task<NpgsqlConnection> OpenAdminConnectionAsync(string database)
    {
        var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database, Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private async Task ExecuteAsAdminAsync(string sql)
    {
        var admin = new NpgsqlConnection(_container.GetConnectionString());
        await using (admin)
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
#pragma warning disable CA2100 // Identifiers are quoted by Quote and come from the tests.
            command.CommandText = sql;
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync();
        }
    }
}
