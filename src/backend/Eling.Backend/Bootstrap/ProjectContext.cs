using Eling.Core;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// Discovered scope chain + user scope and the effective data directory the
/// backend will read/write. Pure data, no IO beyond the directory checks.
/// </summary>
public sealed record ProjectContext(
    ScopeChain Chain,
    UserScope UserScope,
    string EffectiveDataDir,
    bool IsUserHome)
{
    /// <summary>
    /// Write-target project scope: the chain head when initialized, otherwise a
    /// placeholder rooted at the working directory. A placeholder is a pure
    /// path value — it never creates <c>.eling</c> on disk; only
    /// <c>memory_init_project</c> may do that after user consent.
    /// </summary>
    public ProjectScope ProjectScope => Chain.Head ?? new ProjectScope(Chain.Cwd);

    public bool Uninitialized => !Chain.IsInitialized && !IsUserHome;

    public string Posture =>
        IsUserHome ? "user-home" :
        Chain.HasOwnScope ? "own-scope" :
        Chain.IsInitialized ? "ancestor-scope" :
        "uninitialized";

    public static ProjectContext Discover()
    {
        var chain = ScopeChain.Discover();
        var userScope = UserScope.Resolve(Environment.GetEnvironmentVariable("ELING_USER_SCOPE"));
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var isUserHome = !string.IsNullOrWhiteSpace(userHome) &&
            string.Equals(
                chain.Cwd.TrimEnd(Path.DirectorySeparatorChar),
                userHome.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

        // User-home sessions (no active project repository) strictly use
        // ~/.config/eling (global data directory) and never pollute the home
        // directory with ~/.eling. An uninitialized project gets its .eling
        // path as a VALUE only — the directory is never created here.
        var effectiveDataDir = isUserHome
            ? userScope.GlobalDataDirectory
            : chain.Head?.DataDirectory
              ?? Path.Combine(chain.Cwd, ProjectScope.DataDirectoryName);

        // Project .eling directories are created ONLY by memory_init_project
        // after user consent. The user scope runtime dir stays auto-created.
        Directory.CreateDirectory(userScope.RuntimeDirectory);

        return new ProjectContext(chain, userScope, effectiveDataDir, isUserHome);
    }
}
