using Eling.Core;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// Discovered project + user scopes and the effective data directory the
/// backend will read/write. Pure data, no IO beyond the directory checks.
/// </summary>
public sealed record ProjectContext(
    ProjectScope ProjectScope,
    UserScope UserScope,
    string EffectiveDataDir,
    bool IsUserHome)
{
    public static ProjectContext Discover()
    {
        var projectScope = ProjectScope.Discover();
        var userScope = UserScope.Resolve(Environment.GetEnvironmentVariable("ELING_USER_SCOPE"));
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var isUserHome = !string.IsNullOrWhiteSpace(userHome) &&
            string.Equals(
                projectScope.Root.TrimEnd(Path.DirectorySeparatorChar),
                userHome.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

        // If the backend is spawned directly at user home without an active
        // project repository, strictly use ~/.config/eling (global data
        // directory) and never pollute user home with ~/.eling.
        var effectiveDataDir = isUserHome
            ? userScope.GlobalDataDirectory
            : projectScope.DataDirectory;

        Directory.CreateDirectory(effectiveDataDir);
        Directory.CreateDirectory(userScope.RuntimeDirectory);

        return new ProjectContext(projectScope, userScope, effectiveDataDir, isUserHome);
    }
}
