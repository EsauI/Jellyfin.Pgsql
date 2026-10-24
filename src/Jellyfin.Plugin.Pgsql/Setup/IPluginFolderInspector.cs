using System.Collections.Generic;

namespace Jellyfin.Plugin.Pgsql.Setup;

/// <summary>
/// Finds the plugin folders Jellyfin could load the database provider from.
/// </summary>
public interface IPluginFolderInspector
{
    /// <summary>
    /// Lists the provider folders in the order Jellyfin considers them.
    /// </summary>
    /// <returns>The folder names.</returns>
    IReadOnlyList<string> FindProviderFolders();
}
