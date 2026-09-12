namespace Eling.Core.Scope;

public static class CentralLogDirectory
{
    public const string DirectoryName = "logs"; // subdir under the eling root

    /// <summary>
    /// Resolves the central Eling log directory under the user-scoped
    /// XDG-style "local share" location: ~/.local/share/eling/logs/ on every
    /// platform. Honours XDG_DATA_HOME when set; otherwise falls back to
    /// ~/.local/share.
    /// </summary>
    public static string Resolve(string? xdgDataHome = null, string? userHome = null)
    {
        var envXdg = !string.IsNullOrWhiteSpace(xdgDataHome)
            ? xdgDataHome
            : Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        var home = string.IsNullOrWhiteSpace(userHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userHome;
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        // The XDG-style data root is computed uniformly from userHome on every
        // platform. On Windows, userHome is SpecialFolder.UserProfile, so the
        // default data root is <user-home>/.local/share — matching the
        // convention already established by other dev tools.
        // %LOCALAPPDATA% is intentionally NOT used.
        var dataRoot = !string.IsNullOrWhiteSpace(envXdg)
            ? envXdg!
            : Path.Combine(home, ".local", "share");

        // Path.Combine handles both POSIX and Windows path separators
        // transparently, so an XDG_DATA_HOME of "C:\some-folder" works on Windows and
        // "/data" works on Linux/macOS.
        var path = Path.Combine(dataRoot, "eling", DirectoryName);
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
