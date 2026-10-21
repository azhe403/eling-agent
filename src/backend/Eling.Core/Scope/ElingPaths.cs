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
    public const string AuditDirName = "audit";
    public const string MemoryDbFileName = "memory.db";
    public const string LegacyMemoryDbFileName = "index.db";
    public const string CodebaseDbFileName = "codebase.db";
    public const string ProjectsDbFileName = "projects.db";
    public const string ProjectsDirName = "projects";

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
    /// The central, machine-global audit root: &lt;dataRoot&gt;/audit. Global on
    /// purpose — entries carry their originating scope and projectRoot as fields,
    /// so one store serves every project. Resolved through ResolveDataDir so the
    /// ELING_DATA_DIR override isolates test scopes exactly as it already does
    /// for logs and codebase.
    /// </summary>
    public static string ResolveAuditDir(
        string? xdgDataHome = null,
        string? userHome = null,
        string? elingDataDir = null)
    {
        var dataRoot = ResolveDataDir(xdgDataHome, userHome, elingDataDir);
        return Path.Combine(dataRoot, AuditDirName);
    }

    /// <summary>
    /// The project registry, one global store beside memory.db rather than a
    /// file per project: it has to be enumerable without walking a directory,
    /// because enumerating by directory is the problem it exists to solve.
    /// Pure: computes the path only, never touches disk.
    /// </summary>
    public static string ResolveProjectsDbPath(
        string? xdgDataHome = null,
        string? userHome = null,
        string? elingDataDir = null)
    {
        var dataRoot = ResolveDataDir(xdgDataHome, userHome, elingDataDir);
        return Path.Combine(dataRoot, ProjectsDbFileName);
    }

    /// <summary>
    /// Per-project local-memory shards. Path:
    /// $DATA/eling/projects/&lt;project-name&gt;-&lt;hash&gt;/.
    /// Same name+hash scheme as <see cref="ResolveCodebaseDbPath"/> so
    /// worktree-shared canonical roots land on one shard. Caller passes the
    /// canonical root (git main toplevel, fallback cwd root).
    /// Pure: computes the path only, never touches disk.
    /// </summary>
    public static string ResolveProjectLocalDir(
        string canonicalRoot,
        string? xdgDataHome = null,
        string? userHome = null,
        string? elingDataDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        var fullPath = Path.GetFullPath(canonicalRoot);
        var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var projectName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(projectName)) projectName = "project";
        foreach (var invalid in Path.GetInvalidFileNameChars()) projectName = projectName.Replace(invalid, '-');
        var hash = ComputeProjectHash(normalized);
        var dir = Path.Combine(ResolveDataDir(xdgDataHome, userHome, elingDataDir), ProjectsDirName);
        return Path.Combine(dir, $"{projectName}-{hash}");
    }

    /// <summary>
    /// True when the workspace must not participate in codebase indexing:
    /// no DB, no watcher, no dropdown or federation entry. Excluded when at
    /// or under the built-in temporal root (<c>.config/openchamber/chats</c>
    /// under the user home — chat sessions are temporal checkouts, never
    /// indexed), when it is one of the product's own transient workspaces
    /// under the OS temp directory, or any root listed in
    /// <c>ELING_CODEBASE_EXCLUDE</c> (a
    /// <see cref="Path.PathSeparator"/>-separated list for other temporal
    /// roots). Pure: only computes, never touches disk or the process
    /// environment beyond reading the variable. Changing the variable needs
    /// a restart.
    /// </summary>
    public static bool IsCodebaseExcluded(string workspaceRoot, string? excludeRoots = null, string? userHome = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return true;
        var cwd = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var defRoot in DefaultExcludedRoots(userHome))
        {
            if (IsAtOrUnder(cwd, defRoot)) return true;
        }
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

    /// <summary>
    /// Prefixes of the throwaway workspaces the product creates under the OS
    /// temp directory.
    /// </summary>
    /// <remarks>
    /// This list is about INDEXING, and only about indexing: a workspace
    /// matching one of these prefixes gets no codebase index, runs no
    /// watcher, and never appears in the codebase dropdown. An index for a
    /// throwaway is a file nobody will ever read again, in a global store
    /// that nothing ever cleans.
    /// <para>
    /// The catalogue is a separate concern with a wider rule: a scratch
    /// workspace is any folder under the temp area, and it is
    /// <see cref="IsTemporaryPath"/> that says so. The prefixes here are the
    /// narrow set the product names itself; the catalogue's rule is the area.
    /// </para>
    /// </remarks>
    private static readonly string[] TransientWorkspacePrefixes =
    [
        "eling-smoke-",
        "eling-dash-",
        "eling-stdio-",
        "eling-validate-",
    ];

    /// <summary>
    /// True when the path lives under the OS temp directory.
    /// </summary>
    /// <remarks>
    /// The temp directory is a scratch area, not a place projects live: a
    /// validation run, a smoke test, a test fixture and a scratch checkout
    /// all make folders there. A workspace in that area is a throwaway, and a
    /// throwaway must not become a catalogue row - the catalogue prunes a row
    /// only when its folder is GONE, so a scratch folder that outlives the
    /// process that made it would keep its row, and its place in the
    /// dashboard's workspace list, for good.
    /// <para>
    /// The rule is about the area, not about a name: a prefix list would
    /// leave the next throwaway to leak, and the leak is what accumulates.
    /// The prefix list in <see cref="DefaultExcludedRoots"/> is a different
    /// concern - it keeps the product's own throwaways out of the codebase
    /// index.
    /// </para>
    /// </remarks>
    public static bool IsTemporaryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // The comparison is spelled out here rather than delegated to
        // IsAtOrUnder: that helper's third check is a StartsWith without a
        // path boundary, so it only holds because Path.GetTempPath() happens
        // to end with a separator. Spell it out and the guarantee does not
        // depend on that accident -- a sibling folder whose name merely
        // starts with the temp folder's name ("TempX" beside "Temp") is a
        // different folder, and must not count as scratch.
        var temp = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (temp.Length == 0) return false;

        return string.Equals(full, temp, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(temp + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> DefaultExcludedRoots(string? userHome)
    {
        var home = string.IsNullOrWhiteSpace(userHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userHome!;
        yield return Path.Combine(home, ".config", "openchamber", "chats");
        yield return Path.Combine(home, ".config", "opencode");
        yield return Path.Combine(home, ".config", "openchamber");
        foreach (var prefix in TransientWorkspacePrefixes)
        {
            yield return Path.Combine(Path.GetTempPath(), prefix);
        }
    }

    private static bool IsAtOrUnder(string cwd, string root)
    {
        var full = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length == 0) return false;
        return cwd.Equals(full, StringComparison.OrdinalIgnoreCase)
            || cwd.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || cwd.StartsWith(root, StringComparison.OrdinalIgnoreCase);
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
        // 16 hex chars (64-bit) is enough for sharding and stays unique for any realistic project count.
        return sb.ToString()[..16];
    }
}
