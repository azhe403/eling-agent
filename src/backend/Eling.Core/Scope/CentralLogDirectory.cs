using Microsoft.Extensions.Logging;

namespace Eling.Core.Scope;

public static class CentralLogDirectory
{
    public const string DirectoryName = "logs"; // subdir under the eling data root

    /// <summary>
    /// Resolves the central Eling log directory under the user-scoped
    /// XDG-style data location. Delegates to <see cref="ElingPaths.ResolveDataDir"/>
    /// for consistent cross-platform resolution.
    /// </summary>
    public static string Resolve(string? xdgDataHome = null, string? userHome = null, string? elingDataDir = null, ILogger? logger = null)
    {
        var basePath = ElingPaths.ResolveDataDir(xdgDataHome, userHome, elingDataDir);
        var path = Path.Combine(basePath, DirectoryName);
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            // Best-effort directory ensure; caller proceeds with the path regardless.
            logger?.LogDebug(ex, "Central log directory create failed at {Path}", path);
        }
        return path;
    }
}
