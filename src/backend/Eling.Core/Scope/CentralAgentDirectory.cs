namespace Eling.Core.Scope;

public static class CentralAgentDirectory
{
    /// <summary>
    /// Resolves the central Eling agent directory under ~/.local/share/eling/
    /// for global configuration and data (workspaces, provider, chats).
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

        var envXdg = !string.IsNullOrWhiteSpace(xdgDataHome)
            ? xdgDataHome
            : Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        var home = string.IsNullOrWhiteSpace(userHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userHome;
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        var dataRoot = !string.IsNullOrWhiteSpace(envXdg)
            ? envXdg!
            : Path.Combine(home, ".local", "share");

        var basePath = Path.Combine(dataRoot, "eling");
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
