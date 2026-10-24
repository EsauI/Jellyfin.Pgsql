using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Pgsql.Api;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
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
    public void Plugin_ExposesTheEmbeddedConfigurationPage()
    {
        var page = Assert.Single(new Plugin(NSubstitute.Substitute.For<MediaBrowser.Common.Configuration.IApplicationPaths>(), NSubstitute.Substitute.For<MediaBrowser.Model.Serialization.IXmlSerializer>()).GetPages());

        Assert.Contains(page.EmbeddedResourcePath, typeof(Plugin).Assembly.GetManifestResourceNames());
    }
}
