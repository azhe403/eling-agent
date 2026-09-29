using System.Security.Cryptography;
using System.Text;

namespace Eling.Core.Scope;

/// <summary>
/// Cross-platform standard for Eling locations.
/// Config: $XDG_CONFIG_HOME/eling or ~/.config/eling.
/// Data: $XDG_DATA_HOME/eling or ~/.local/share/eling.
/// Runtime: $XDG_DATA_HOME/eling/runtime or ~/.local/share/eling/runtime.
/// Codebase index: $XDG_DATA_HOME/eling/codebase/&lt;hash&gt;.db (global, not in the project's .eling).
/// Deliberately uniform across all operating systems (no %APPDATA%/%LOCALAPPDATA%),
/// following the convention already used by CentralLogDirectory/CentralAgentDirectory.
/// Pure: computes paths only, never touches disk.
/// </summary>
public static class ElingPaths
{
    public const string AppName = "eling";
    public const string DotElingDirName = ".eling";
    public const string MemoriesDirName = "memories";
    public const string RuntimeDirName = "runtime";
    public const string CodebaseDirName = "codebase";
    public const string MemoryDbFileName = "memory.db";
    public const string LegacyMemoryDbFileName = "index.db";
    public const string CodebaseDbFileName = "codebase.db";

    public static string ResolveConfigDir(
        string? xdgConfigHome = null,
        string? userHome = null,
        string? elingConfigDir = null)
    {
        var envOverride = !string.IsNullOrWhiteSpace(elingConfigDir)
            ? elingConfigDir
            : Environment.GetEnvironmentVariable("ELING_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return Path.GetFullPath(envOverride!);
        }

        var envXdg = !string.IsNullOrWhiteSpace(xdgConfigHome)
            ? xdgConfigHome
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        var home = string.IsNullOrWhiteSpace(userHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userHome!;
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        var configRoot = !string.IsNullOrWhiteSpace(envXdg)
            ? envXdg!
            : Path.Combine(home, ".config");

        return Path.Combine(configRoot, AppName);
    }

    public static string ResolveDataDir(
        string? xdgDataHome = null,
        string? userHome = null,
        string? elingDataDir = null)
    {
        var envOverride = !string.IsNullOrWhiteSpace(elingDataDir)
            ? elingDataDir
            : Environment.GetEnvironmentVariable("ELING_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return Path.GetFullPath(envOverride!);
        }

        var envXdg = !string.IsNullOrWhiteSpace(xdgDataHome)
            ? xdgDataHome
            : Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        var home = string.IsNullOrWhiteSpace(userHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userHome!;
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        var dataRoot = !string.IsNullOrWhiteSpace(envXdg)
            ? envXdg!
            : Path.Combine(home, ".local", "share");

        return Path.Combine(dataRoot, AppName);
    }

    public static string ResolveRuntimeDir(
        string? xdgDataHome = null,
        string? userHome = null,
        string? elingDataDir = null)
    {
        var dataRoot = ResolveDataDir(xdgDataHome, userHome, elingDataDir);
        return Path.Combine(dataRoot, RuntimeDirName);
    }

    public static string ResolveCodebaseDir(
        string? xdgDataHome = null,
        string? userHome = null,
        string? elingDataDir = null)
    {
        var dataRoot = ResolveDataDir(xdgDataHome, userHome, elingDataDir);
        return Path.Combine(dataRoot, CodebaseDirName);
    }

    /// <summary>
    /// True when the workspace must not participate in codebase indexing:
    /// no DB, no watcher, no dropdown or federation entry. Excluded when at
    /// or under the built-in temporal root (<c>.config/openchamber/chats</c>
    /// under the user home — chat sessions are temporal checkouts, never
    /// indexed) or any root listed in <c>ELING_CODEBASE_EXCLUDE</c> (a
    /// <see cref="Path.PathSeparator"/>-separated list for other temporal
    /// roots). Pure: only computes, never touches disk or the process
    /// environment beyond reading the variable. Changing the variable needs
    /// a restart.
    /// </summary>
    public static bool IsCodebaseExcluded(string workspaceRoot, string? excludeRoots = null, string? userHome = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return true;
        var cwd = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (IsAtOrUnder(cwd, DefaultChatsRoot(userHome))) return true;
        var raw = excludeRoots ?? Environment.GetEnvironmentVariable("ELING_CODEBASE_EXCLUDE");
        if (string.IsNullOrWhiteSpace(raw)) return false;
        foreach (var entry in raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string full;
            try
            {
                full = Path.GetFullPath(entry).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Never swallow this one. A malformed entry is dropped silently
                // otherwise, and the workspace the user meant to exclude —
                // usually a chat or scratch checkout — gets indexed anyway.
                throw new InvalidOperationException(
                    $"Codebase exclusion entry '{entry}' is not a valid path. Entries must be "
                    + $"absolute directory paths separated by '{Path.PathSeparator}'. Check "
                    + "ELING_CODEBASE_EXCLUDE and any exclude roots passed in.", ex);
            }
            if (IsAtOrUnder(cwd, full)) return true;
        }

        return false;
    }

    /// <summary>
    /// Validates caller-supplied codebase roots before they reach the indexer
    /// or a federated read. A root is accepted only when it is a fully
    /// qualified path that is not excluded by <see cref="IsCodebaseExcluded"/>.
    ///
    /// Both halves matter. Without the qualified-path check a relative entry
    /// is silently resolved against the process working directory, so a
    /// registry label like <c>"UserScope"</c> becomes an arbitrary directory.
    /// Without the exclusion check a path parameter re-admits a temporal
    /// checkout that the registry would never list — the gate has to hold for
    /// every entry point, not only for registry-derived roots.
    ///
    /// Blank entries are dropped. The first offender throws
    /// <see cref="ArgumentException"/> so callers can surface a 400 naming it.
    /// </summary>
    public static IReadOnlyList<string> EnsureCodebaseRootsAllowed(
        IEnumerable<string>? roots,
        string? excludeRoots = null,
        string? userHome = null)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var accepted = new List<string>();
        foreach (var raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!Path.IsPathFullyQualified(raw))
                throw new ArgumentException($"Codebase root must be an absolute path: '{raw}'.", nameof(roots));

            string full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Codebase root is not a valid path: '{raw}'.", nameof(roots), ex);
            }

            if (IsCodebaseExcluded(full, excludeRoots, userHome))
                throw new ArgumentException($"Codebase root is excluded from indexing: '{full}'.", nameof(roots));

            accepted.Add(full);
        }

        return accepted;
    }

    private static string DefaultChatsRoot(string? userHome)
    {
        var home = string.IsNullOrWhiteSpace(userHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userHome!;
        return Path.Combine(home, ".config", "openchamber", "chats");
    }

    private static bool IsAtOrUnder(string cwd, string root)
    {
        var full = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length == 0) return false;
        return cwd.Equals(full, StringComparison.OrdinalIgnoreCase)
            || cwd.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve per-project codebase DB path in global store.
    /// Path: $DATA/eling/codebase/&lt;project-name&gt;-&lt;hash&gt;.db.
    /// Project name comes from project folder; hash keeps same-named projects separate.
    /// Pure: computes paths only, never touches disk.
    /// </summary>
    public static string ResolveCodebaseDbPath(
        string projectRoot,
        string? xdgDataHome = null,
        string? userHome = null,
        string? elingDataDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var fullPath = Path.GetFullPath(projectRoot);
        var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var projectName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(projectName)) projectName = "project";
        foreach (var invalid in Path.GetInvalidFileNameChars()) projectName = projectName.Replace(invalid, '-');
        var hash = ComputeProjectHash(normalized);
        var dir = ResolveCodebaseDir(xdgDataHome, userHome, elingDataDir);
        return Path.Combine(dir, $"{projectName}-{hash}.db");
    }

    private static string ComputeProjectHash(string normalizedPath)
    {
        var bytes = Encoding.UTF8.GetBytes(normalizedPath);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2"));
        // 16 hex chars (64-bit) cukup untuk shard, tetap unik untuk jumlah proyek wajar.
        return sb.ToString()[..16];
    }
}
