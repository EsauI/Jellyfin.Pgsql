using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Plugin.Pgsql.Api;
using Jellyfin.Plugin.Pgsql.Setup;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Api;

public class PgsqlSetupControllerTests
{
    [Fact]
    public void Controller_RequiresAnAdministrator()
    {
        var authorize = typeof(PgsqlSetupController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(Policies.RequiresElevation, authorize.Policy);
    }

    [Fact]
    public void EveryAction_IsReachableOnlyThroughTheAuthorizedController()
    {
        var actions = typeof(PgsqlSetupController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes().OfType<HttpMethodAttribute>().Any())
            .ToList();

        Assert.Equal(4, actions.Count);
        Assert.DoesNotContain(actions, m => m.GetCustomAttribute<AllowAnonymousAttribute>() != null);
    }

    [Fact]
    public void SwitchToSqlite_WhenTheServerAlreadyUsesSqlite_WritesNothingAndDoesNotRestart()
    {
        var store = Substitute.For<IDatabaseConfigurationStore>();
        store.SqliteDatabaseExists.Returns(true);
        store.Read().Returns(new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-SQLite" });
        var systemManager = Substitute.For<ISystemManager>();
        var controller = new PgsqlSetupController(
            new GuidedSetupService(store, Substitute.For<IConnectionChecker>(), Substitute.For<IPluginFolderInspector>()),
            systemManager);

        var result = controller.SwitchToSqlite();

        Assert.False(result.Value!.Succeeded);
        store.DidNotReceive().SaveSqlite();
        systemManager.DidNotReceive().Restart();
    }

    [Fact]
    public async Task Apply_WhenItSucceeds_RestartsExactlyOnce()
    {
        var (controller, store, systemManager) = Create(TargetState.JellyfinSchema);

        var result = await controller.Apply(new ConnectionSettings { Host = "db", Username = "jf", Password = "pw" }, TestContext.Current.CancellationToken);

        Assert.True(result.Value!.Succeeded);
        store.Received(1).SavePostgreSql(Arg.Any<string>());
        systemManager.Received(1).Restart();
    }

    [Fact]
    public async Task Apply_WhenItIsRefused_NeverRestarts()
    {
        var (controller, store, systemManager) = Create(TargetState.Other);

        var result = await controller.Apply(new ConnectionSettings { Host = "db", Username = "jf", Password = "pw" }, TestContext.Current.CancellationToken);

        Assert.False(result.Value!.Succeeded);
        store.DidNotReceiveWithAnyArgs().SavePostgreSql(default!);
        systemManager.DidNotReceive().Restart();
    }

    [Fact]
    public void SwitchToSqlite_WhenItSucceeds_RestartsExactlyOnce()
    {
        var (controller, store, systemManager) = Create(TargetState.JellyfinSchema);
        store.Read().Returns(new DatabaseConfigurationOptions { DatabaseType = "PLUGIN_PROVIDER" });
        store.SqliteDatabaseExists.Returns(true);

        var result = controller.SwitchToSqlite();

        Assert.True(result.Value!.Succeeded);
        store.Received(1).SaveSqlite();
        systemManager.Received(1).Restart();
    }

    [Fact]
    public void SwitchToSqlite_WhenJellyfinDbIsMissing_NeverRestarts()
    {
        var (controller, store, systemManager) = Create(TargetState.JellyfinSchema);
        store.Read().Returns(new DatabaseConfigurationOptions { DatabaseType = "PLUGIN_PROVIDER" });
        store.SqliteDatabaseExists.Returns(false);

        var result = controller.SwitchToSqlite();

        Assert.False(result.Value!.Succeeded);
        systemManager.DidNotReceive().Restart();
    }

    [Fact]
    public void Plugin_ExposesTheEmbeddedConfigurationPage()
    {
        var page = Assert.Single(new Plugin(NSubstitute.Substitute.For<MediaBrowser.Common.Configuration.IApplicationPaths>(), NSubstitute.Substitute.For<MediaBrowser.Model.Serialization.IXmlSerializer>()).GetPages());

        Assert.Contains(page.EmbeddedResourcePath, typeof(Plugin).Assembly.GetManifestResourceNames());
    }

    private static (PgsqlSetupController Controller, IDatabaseConfigurationStore Store, ISystemManager SystemManager) Create(TargetState target)
    {
        var store = Substitute.For<IDatabaseConfigurationStore>();
        store.Read().Returns(new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-SQLite" });
        var checker = Substitute.For<IConnectionChecker>();
        checker.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ConnectionAssessment
        {
            Connected = true,
            CanCreateDatabase = true,
            OwnsDatabase = true,
            TargetState = target,
        });
        var systemManager = Substitute.For<ISystemManager>();
        var folders = Substitute.For<IPluginFolderInspector>();
        folders.FindProviderFolders().Returns([]);
        return (new PgsqlSetupController(new GuidedSetupService(store, checker, folders), systemManager), store, systemManager);
    }
}
