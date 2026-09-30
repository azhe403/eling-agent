namespace Eling.Core.Scope;

public static class CentralAgentDirectory
{
    /// <summary>
    /// Resolves the central Eling agent directory under data-root/eling/
    /// for global configuration and data (workspaces, provider, chats).
    /// Data root follows <see cref="ElingPaths.ResolveDataDir"/>.
    /// </summary>
    public static string Resolve(string? subDirectory = null, string? xdgDataHome = null, string? userHome = null)
    {
        var envAgentDir = Environment.GetEnvironmentVariable("ELING_AGENT_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(envAgentDir))
        {
            var customBase = Path.GetFullPath(envAgentDir);
            var customPath = string.IsNullOrWhiteSpace(subDirectory)
                ? customBase
                : Path.Combine(customBase, subDirectory);
            try { Directory.CreateDirectory(customPath); } catch { }
            return customPath;
        }

        var basePath = ElingPaths.ResolveDataDir(xdgDataHome, userHome);
        var path = string.IsNullOrWhiteSpace(subDirectory)
            ? basePath
            : Path.Combine(basePath, subDirectory);

        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
        }
        return path;
    }
}
