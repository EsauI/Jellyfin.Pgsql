using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// <see cref="IPluginFolderInspector"/> that looks at the plugins directory.
/// </summary>
/// <param name="applicationPaths">The server paths.</param>
public class PluginFolderInspector(IApplicationPaths applicationPaths) : IPluginFolderInspector
{
    /// <inheritdoc />
    public IReadOnlyList<string> FindProviderFolders()
    {
        var path = applicationPaths.PluginsPath;
        if (!Directory.Exists(path))
        {
            return [];
        }

        // Mirrors how the server picks the provider folder (case-insensitive prefix, default ordering), so the first
        // folder listed here is the one Jellyfin actually loads.
        return Directory.EnumerateDirectories(path)
            .Select(d => Path.GetFileName(d))
            .Where(n => n.StartsWith(Plugin.PluginName, StringComparison.OrdinalIgnoreCase))
            .Order()
            .ToList();
    }
}
