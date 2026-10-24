using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Plugin.Pgsql.Setup;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Setup;

public class GuidedSetupServiceTests
{
    private static readonly ConnectionSettings Settings = new() { Host = "db", Database = "jf", Username = "jf", Password = "pw" };

    private readonly IDatabaseConfigurationStore _store = Substitute.For<IDatabaseConfigurationStore>();
    private readonly IConnectionChecker _checker = Substitute.For<IConnectionChecker>();
    private readonly IPluginFolderInspector _folders = Substitute.For<IPluginFolderInspector>();

    public GuidedSetupServiceTests()
    {
        _store.Read().Returns(new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-SQLite" });
        _folders.FindProviderFolders().Returns([]);
    }

    [Fact]
    public async Task Apply_RefusesASetUpServerWithAnEmptyTarget_AndWritesNothing()
    {
        _store.IsServerSetUp.Returns(true);
        Target(TargetState.Empty);

        var result = await Service().ApplyAsync(Settings, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("migrat", result.Message, StringComparison.OrdinalIgnoreCase);
        _store.DidNotReceiveWithAnyArgs().SavePostgreSql(default!);
    }

    [Fact]
    public async Task Apply_AllowsANewServerWithAnEmptyTarget()
    {
        _store.IsServerSetUp.Returns(false);
        Target(TargetState.Empty);

        var result = await Service().ApplyAsync(Settings, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        _store.Received(1).SavePostgreSql(Arg.Is<string>(s => s.Contains("Host=db", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Apply_AllowsASetUpServerWhenTheTargetAlreadyHasTheJellyfinSchema()
    {
        _store.IsServerSetUp.Returns(true);
        Target(TargetState.JellyfinSchema);

        var result = await Service().ApplyAsync(Settings, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        _store.Received(1).SavePostgreSql(Arg.Any<string>());
    }

    [Fact]
    public async Task Apply_RefusesATargetWithForeignTables()
    {
        Target(TargetState.Other);

        var result = await Service().ApplyAsync(Settings, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        _store.DidNotReceiveWithAnyArgs().SavePostgreSql(default!);
    }

    [Fact]
    public async Task Apply_RefusesARoleThatCannotCreateDatabases()
    {
        Target(TargetState.Empty, canCreate: false);

        var result = await Service().ApplyAsync(Settings, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("CREATEDB", result.Message, StringComparison.Ordinal);
        _store.DidNotReceiveWithAnyArgs().SavePostgreSql(default!);
    }

    [Fact]
    public async Task Apply_RefusesInvalidSettingsWithoutConnecting()
    {
        var result = await Service().ApplyAsync(new ConnectionSettings { Host = " ", Username = "jf" }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        await _checker.DidNotReceiveWithAnyArgs().CheckAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Test_ReportsTheBlockersForASetUpServerWithAnEmptyTarget()
    {
        _store.IsServerSetUp.Returns(true);
        Target(TargetState.Empty);

        var assessment = await Service().TestAsync(Settings, TestContext.Current.CancellationToken);

        Assert.False(assessment.CanApply);
        Assert.Single(assessment.Blockers);
    }

    [Fact]
    public async Task Test_KeepsTheStoredPasswordWhenNoneIsSubmitted()
    {
        _store.Read().Returns(new DatabaseConfigurationOptions
        {
            DatabaseType = "PLUGIN_PROVIDER",
            CustomProviderOptions = new CustomDatabaseOptions { PluginName = "PostgreSQL", PluginAssembly = "Jellyfin.Plugin.Pgsql.dll", ConnectionString = "Host=db;Username=jf;Password=stored" },
        });
        Target(TargetState.Empty);

        await Service().TestAsync(new ConnectionSettings { Host = "db", Username = "jf" }, TestContext.Current.CancellationToken);

        await _checker.Received(1).CheckAsync(Arg.Is<string>(s => s.Contains("Password=stored", StringComparison.Ordinal)), Arg.Any<System.Threading.CancellationToken>());
    }

    [Fact]
    public void SwitchToSqlite_WritesSqlite()
    {
        _store.SqliteDatabaseExists.Returns(true);

        var result = Service().SwitchToSqlite();

        Assert.True(result.Succeeded);
        _store.Received(1).SaveSqlite();
    }

    [Fact]
    public void SwitchToSqlite_StatesItIsARollbackAndShowsHowOldJellyfinDbIs()
    {
        _store.SqliteDatabaseExists.Returns(true);
        _store.SqliteLastModifiedUtc.Returns(new DateTime(2026, 10, 1, 12, 30, 0, DateTimeKind.Utc));

        var result = Service().SwitchToSqlite();

        Assert.Contains("2026-10-01 12:30 UTC", result.Message, StringComparison.Ordinal);
        Assert.Contains("rollback", result.Message, StringComparison.Ordinal);
        Assert.Contains("only in PostgreSQL", result.Message, StringComparison.Ordinal);
        Assert.Contains("NOT appear in SQLite", result.Message, StringComparison.Ordinal);
        Assert.Contains("kept untouched", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSettings_ProvidesTheSwitchBackNoticeForTheConfirmationDialog()
    {
        _store.SqliteDatabaseExists.Returns(true);
        _store.SqliteLastModifiedUtc.Returns(new DateTime(2026, 10, 1, 12, 30, 0, DateTimeKind.Utc));

        var notice = Service().GetSettings().SwitchBackNotice;

        Assert.Contains("2026-10-01 12:30 UTC", notice, StringComparison.Ordinal);
        Assert.Contains("NOT appear in SQLite", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSettings_HasNoSwitchBackNoticeWithoutASqliteDatabase()
    {
        Assert.Null(Service().GetSettings().SwitchBackNotice);
    }

    [Fact]
    public void SwitchToSqlite_RefusesWhenJellyfinDbIsMissing()
    {
        _store.SqliteDatabaseExists.Returns(false);

        var result = Service().SwitchToSqlite();

        Assert.False(result.Succeeded);
        _store.DidNotReceive().SaveSqlite();
    }

    private GuidedSetupService Service() => new(_store, _checker, _folders);

    private void Target(TargetState state, bool canCreate = true)
        => _checker.CheckAsync(Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>()).Returns(new ConnectionAssessment
        {
            Connected = true,
            ServerVersion = "18.0",
            CanCreateDatabase = canCreate,
            OwnsDatabase = true,
            TargetState = state,
        });
}
