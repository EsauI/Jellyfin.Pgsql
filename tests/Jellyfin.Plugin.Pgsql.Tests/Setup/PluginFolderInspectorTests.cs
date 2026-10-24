using System;
using System.IO;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Plugin.Pgsql.Setup;
using MediaBrowser.Common.Configuration;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Setup;

public sealed class PluginFolderInspectorTests : IDisposable
{
    private readonly string _plugins = Directory.CreateTempSubdirectory("jf-plugins-").FullName;

    public void Dispose() => Directory.Delete(_plugins, recursive: true);

    [Fact]
    public void FindProviderFolders_ListsPostgreSqlFoldersInOrdinalOrder()
    {
        Directory.CreateDirectory(Path.Combine(_plugins, "PostgreSQL_1.2.0.0"));
        Directory.CreateDirectory(Path.Combine(_plugins, "PostgreSQL_1.1.0.0"));
        Directory.CreateDirectory(Path.Combine(_plugins, "Other_1.0.0.0"));

        Assert.Equal(["PostgreSQL_1.1.0.0", "PostgreSQL_1.2.0.0"], Inspector().FindProviderFolders());
    }

    [Fact]
    public void FindProviderFolders_ReturnsNothingWhenThePluginsPathIsMissing()
    {
        Assert.Empty(new PluginFolderInspector(Paths(Path.Combine(_plugins, "missing"))).FindProviderFolders());
    }

    [Fact]
    public void GetSettings_WarnsWhenSeveralFoldersExist_AndDeletesNothing()
    {
        Directory.CreateDirectory(Path.Combine(_plugins, "PostgreSQL_1.0.0.0"));
        Directory.CreateDirectory(Path.Combine(_plugins, "PostgreSQL_1.1.0.0"));
        var service = new GuidedSetupService(Store(), Substitute.For<IConnectionChecker>(), Inspector());

        var warning = Assert.Single(service.GetSettings().Warnings);

        Assert.Contains("PostgreSQL_1.0.0.0", warning, StringComparison.Ordinal);
        Assert.Contains("PostgreSQL_1.1.0.0", warning, StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetDirectories(_plugins).Length);
    }

    [Fact]
    public void GetSettings_DoesNotWarnForASingleFolder()
    {
        Directory.CreateDirectory(Path.Combine(_plugins, "PostgreSQL_1.0.0.0"));
        var service = new GuidedSetupService(Store(), Substitute.For<IConnectionChecker>(), Inspector());

        Assert.Empty(service.GetSettings().Warnings);
    }

    private static IDatabaseConfigurationStore Store()
    {
        var store = Substitute.For<IDatabaseConfigurationStore>();
        store.Read().Returns(new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-SQLite" });
        return store;
    }

    private static IApplicationPaths Paths(string plugins)
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginsPath.Returns(plugins);
        return paths;
    }

    private PluginFolderInspector Inspector() => new(Paths(_plugins));
}
