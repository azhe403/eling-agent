namespace Eling.Core.Scope;

public sealed class UserScope
{
    /// <summary>Config root: ~/.config/eling (with <see cref="Root"/> kept as a compatibility alias).</summary>
    public string ConfigRoot { get; }

    /// <summary>Historical alias for <see cref="Root"/>.</summary>
    public string Root => ConfigRoot;

    /// <summary>Data root: ~/.local/share/eling.</summary>
    public string DataRoot { get; }

    public string ConfigDirectory { get; }
    public string RuntimeDirectory { get; }

    /// <summary>
    /// The project registry DB, beside memory.db and outside every project's
    /// .eling. Resolved through the same data root as the runtime directory so
    /// one override (ELING_DATA_DIR, or a test's UserScope.Resolve) relocates
    /// both together instead of leaving them pointing at different stores.
    /// </summary>
    public string ProjectsDatabasePath { get; }

    /// <summary>
    /// Global memory storage root — physically separated from any project .eling.
    /// Points to data-root/eling/ itself; FileSystemMemoryStorage appends /memories.
    /// </summary>
    public string GlobalDataDirectory => DataRoot;

    /// <summary>
    /// Legacy location of the global memories store (config-root), for one-time migration/fallback.
    /// </summary>
    public string LegacyGlobalDataDirectory => ConfigRoot;

    /// <summary>Legacy runtime location (config-root/runtime) used as a fallback.</summary>
    public string LegacyRuntimeDirectory => Path.Combine(ConfigRoot, "runtime");

    public UserScope(string root, string? dataRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ConfigRoot = Path.GetFullPath(root);
        DataRoot = string.IsNullOrWhiteSpace(dataRoot)
            ? ConfigRoot
            : Path.GetFullPath(dataRoot);
        ConfigDirectory = Path.Combine(ConfigRoot, "config");
        RuntimeDirectory = ElingPaths.ResolveRuntimeDir(null, null, dataRoot);
        ProjectsDatabasePath = ElingPaths.ResolveProjectsDbPath(null, null, dataRoot);
    }

    public static UserScope Resolve(
        string? overridePath = null,
        string? xdgConfigHome = null,
        string? xdgDataHome = null,
        string? userHome = null,
        string? elingConfigDir = null,
        string? elingDataDir = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            // Isolasi test: satu temp dir untuk config + data.
            var isolated = Path.GetFullPath(overridePath!);
            return new UserScope(isolated, isolated);
        }

        var configRoot = ElingPaths.ResolveConfigDir(xdgConfigHome, userHome, elingConfigDir);
        var dataRoot = ElingPaths.ResolveDataDir(xdgDataHome, userHome, elingDataDir);
        return new UserScope(configRoot, dataRoot);
    }

    /// <summary>
    /// One-time migration from the legacy location (config-root) to the data root:
    /// menyalin memories/*.md + index.db (legacy) ke memory.db bila tujuan masih kosong.
    /// Idempotent, and never deletes the source.
    /// </summary>
    public void EnsureGlobalDataMigrated()
    {
        try
        {
            if (string.Equals(
                ConfigRoot.TrimEnd(Path.DirectorySeparatorChar),
                DataRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var legacyMemories = Path.Combine(LegacyGlobalDataDirectory, "memories");
            var targetMemories = Path.Combine(DataRoot, "memories");
            if (Directory.Exists(legacyMemories) &&
                !Directory.Exists(targetMemories))
            {
                CopyDirectory(legacyMemories, targetMemories);
            }

            var legacyIndex = Path.Combine(LegacyGlobalDataDirectory, "index.db");
            var targetIndex = Path.Combine(DataRoot, "memory.db");
            if (File.Exists(legacyIndex) && !File.Exists(targetIndex))
            {
                Directory.CreateDirectory(DataRoot);
                File.Copy(legacyIndex, targetIndex);
            }
        }
        catch
        {
            // Migrasi best-effort: kegagalan tidak boleh menggagalkan boot.
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var dest = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(dest))
            {
                File.Copy(file, dest);
            }
        }
    }
}