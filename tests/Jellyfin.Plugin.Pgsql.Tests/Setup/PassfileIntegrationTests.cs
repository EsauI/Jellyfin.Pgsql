using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Plugin.Pgsql.Setup;
using Jellyfin.Plugin.Pgsql.Tests.Database;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Setup;

[Trait("Category", "Integration")]
[Collection(PostgreSqlFixtureDefinition.Name)]
public sealed class PassfileIntegrationTests(PostgreSqlFixture fixture) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("jf-passfile-it-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("plain-password")]
    [InlineData("co:lon\\back;semi'quote\"double")]
    public async Task TheProviderConnectsThroughThePassfileTheStoreWrote_AndDatabaseXmlHoldsNoPassword(string password)
    {
        var token = TestContext.Current.CancellationToken;
        var source = await fixture.CreateRestrictedRoleDatabaseAsync(password: password);
        var saved = SaveThroughTheStore(source.ConnectionString);

        Assert.DoesNotContain(password, saved, StringComparison.Ordinal);
        Assert.Contains("Passfile=", saved, StringComparison.Ordinal);

        var viaPassfile = new TestDatabase(saved);
        await using var context = viaPassfile.CreateContext();
        Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync(token));
    }

    [Fact]
    public async Task TheBackupServiceReachesTheMaintenanceDatabaseThroughThePassfile()
    {
        var token = TestContext.Current.CancellationToken;
        var source = await fixture.CreateRestrictedRoleDatabaseAsync(password: "co:lon\\pw");
        var viaPassfile = new TestDatabase(SaveThroughTheStore(source.ConnectionString));

        // The setup above migrated through a pooled session that would otherwise block the clone.
        NpgsqlConnection.ClearAllPools();
        var key = await viaPassfile.Provider.MigrationBackupFast(token);
        await viaPassfile.Provider.DeleteBackup(key);

        Assert.Empty(await fixture.ListDatabasesAsync(source.DatabaseName + "_" + key));
    }

    [Fact]
    public async Task TheCheckerAcceptsAConnectionStringThatOnlyReferencesThePassfile()
    {
        var source = await fixture.CreateRestrictedRoleDatabaseAsync(password: "a:b\\c");
        var saved = SaveThroughTheStore(source.ConnectionString);

        var result = await new ConnectionChecker().CheckAsync(saved, TestContext.Current.CancellationToken);

        Assert.True(result.Connected);
    }

    private string SaveThroughTheStore(string connectionStringWithPassword)
    {
        var manager = Substitute.For<IServerConfigurationManager>();
        manager.GetConfiguration("database").Returns(new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-SQLite" });
        var paths = Substitute.For<IApplicationPaths>();
        paths.ConfigurationDirectoryPath.Returns(_directory);
        DatabaseConfigurationOptions? written = null;
        manager.When(m => m.SaveConfiguration("database", Arg.Any<object>())).Do(call => written = (DatabaseConfigurationOptions)call.ArgAt<object>(1));

        new DatabaseConfigurationStore(manager, paths).SavePostgreSql(connectionStringWithPassword);

        return written!.CustomProviderOptions!.ConnectionString;
    }
}
