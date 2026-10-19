using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests;

public sealed class PluginTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "jellyfin-pgsql-tests", Guid.NewGuid().ToString("N"));

    public PluginTests()
    {
        Directory.CreateDirectory(_rootPath);
    }

    public void Dispose()
    {
        Directory.Delete(_rootPath, recursive: true);
    }

    [Fact]
    public void Name_IsPostgreSql()
    {
        var plugin = CreatePlugin();

        Assert.Equal("PostgreSQL", plugin.Name);
        Assert.Equal("PostgreSQL", Plugin.PluginName);
    }

    [Fact]
    public void Id_IsStableGuid()
    {
        var plugin = CreatePlugin();

        Assert.Equal(Guid.Parse("a9d77ad7-f272-499f-a361-c70646cbf6e3"), plugin.Id);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        var plugin = CreatePlugin();

        Assert.Equal("PostgreSQL database provider for Jellyfin.", plugin.Description);
    }

    [Fact]
    public void Constructor_SetsStaticInstance()
    {
        var plugin = CreatePlugin();

        Assert.Same(plugin, Plugin.Instance);
    }

    private Plugin CreatePlugin()
    {
        var applicationPaths = Substitute.For<IApplicationPaths>();
        applicationPaths.PluginConfigurationsPath.Returns(_rootPath);
        applicationPaths.PluginsPath.Returns(_rootPath);

        return new Plugin(applicationPaths, Substitute.For<IXmlSerializer>());
    }
}
