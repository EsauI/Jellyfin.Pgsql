using System.Linq;
using System.Reflection;
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
    public void Plugin_ExposesTheEmbeddedConfigurationPage()
    {
        var page = Assert.Single(new Plugin(NSubstitute.Substitute.For<MediaBrowser.Common.Configuration.IApplicationPaths>(), NSubstitute.Substitute.For<MediaBrowser.Model.Serialization.IXmlSerializer>()).GetPages());

        Assert.Contains(page.EmbeddedResourcePath, typeof(Plugin).Assembly.GetManifestResourceNames());
    }
}
