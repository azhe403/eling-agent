namespace Eling.Core.Docs;

using Eling.Core.Scope;

/// <summary>
/// Resolves a document path to its ownership, origin, relative path, and root path.
/// Reuses CanonicalProjectRoot and ElingPaths for zero duplicated path logic.
/// </summary>
public sealed class DocScopeResolver
{
    private readonly string _workspaceRoot;
    private readonly string _canonicalRoot;
    private readonly string _projectLocalDocsDir;

    /// <summary>Gets the workspace root directory.</summary>
    public string WorkspaceRoot => _workspaceRoot;

    /// <summary>Gets the canonical project root directory.</summary>
    public string CanonicalRoot => _canonicalRoot;

    public DocScopeResolver(string? workspaceRoot = null)
    {
        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(workspaceRoot);

        _canonicalRoot = CanonicalProjectRoot.Resolve(_workspaceRoot);
        _projectLocalDocsDir = ElingPaths.ResolveProjectLocalDir(_canonicalRoot);
    }

    /// <summary>
    /// Resolves a document path to ownership, origin, relative path, and root path.
    /// </summary>
    public DocScopeResolution Resolve(string absolutePath)
    {
        var fullPath = Path.GetFullPath(absolutePath);

        // Check if it's under project-local shard
        if (IsUnder(fullPath, _projectLocalDocsDir))
        {
            var rel = GetRelative(fullPath, _projectLocalDocsDir);
            return new DocScopeResolution(
                DocOwnership.Managed,
                DocOrigin.Shard,
                rel,
                _projectLocalDocsDir);
        }

        // Check if it's under current repo's .eling/docs
        var currentElingDocs = Path.Combine(_canonicalRoot, ElingPaths.DotElingDirName, "docs");
        if (IsUnder(fullPath, currentElingDocs))
        {
            var rel = GetRelative(fullPath, currentElingDocs);
            return new DocScopeResolution(
                DocOwnership.Managed,
                DocOrigin.Current,
                rel,
                currentElingDocs);
        }

        // Check if it's under current repo's docs/
        var currentDocs = Path.Combine(_canonicalRoot, "docs");
        if (IsUnder(fullPath, currentDocs))
        {
            var rel = GetRelative(fullPath, currentDocs);
            return new DocScopeResolution(
                DocOwnership.External,
                DocOrigin.Current,
                rel,
                currentDocs);
        }

        // Check ancestor .eling directories (walk up from workspace)
        var ancestorEling = FindAncestorElingDocs(_workspaceRoot);
        if (!string.IsNullOrEmpty(ancestorEling) && IsUnder(fullPath, ancestorEling))
        {
            var rel = GetRelative(fullPath, ancestorEling);
            return new DocScopeResolution(
                DocOwnership.Managed,
                DocOrigin.Ancestor,
                rel,
                ancestorEling);
        }

        // Check ancestor docs/ directories
        var ancestorDocs = FindAncestorDocs(_workspaceRoot);
        if (!string.IsNullOrEmpty(ancestorDocs) && IsUnder(fullPath, ancestorDocs))
        {
            var rel = GetRelative(fullPath, ancestorDocs);
            return new DocScopeResolution(
                DocOwnership.External,
                DocOrigin.Ancestor,
                rel,
                ancestorDocs);
        }

        // Global config directory
        var globalDocs = Path.Combine(ElingPaths.ResolveConfigDir(), "docs");
        if (IsUnder(fullPath, globalDocs))
        {
            var rel = GetRelative(fullPath, globalDocs);
            return new DocScopeResolution(
                DocOwnership.Managed,
                DocOrigin.Global,
                rel,
                globalDocs);
        }

        // Fallback: treat as external current (should not happen for indexed paths)
        var fallbackRel = GetRelative(fullPath, _canonicalRoot);
        return new DocScopeResolution(
            DocOwnership.External,
            DocOrigin.Current,
            fallbackRel,
            _canonicalRoot);
    }

    /// <summary>
    /// Gets the index database path for a given origin.
    /// Returns null for External (indexed into workspace's covering DB).
    /// </summary>
    public string? GetIndexDbPath(DocOrigin origin, DocScopeDecision? decision = null)
    {
        return origin switch
        {
            DocOrigin.Shard => Path.Combine(_projectLocalDocsDir, "docs.db"),
            DocOrigin.Current when decision?.Decision != ScopeDecision.Disabled
                => Path.Combine(_canonicalRoot, ElingPaths.DotElingDirName, "docs-index.db"),
            DocOrigin.Global => Path.Combine(ElingPaths.ResolveDataDir(), "docs.db"),
            _ => null, // External and disabled Current have no dedicated index
        };
    }

    private static bool IsUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRelative(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = fullPath.Substring(fullRoot.Length + 1);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string? FindAncestorElingDocs(string start)
    {
        var dir = Path.GetFullPath(start);
        while (dir != null)
        {
            var elingDocs = Path.Combine(dir, ElingPaths.DotElingDirName, "docs");
            if (Directory.Exists(elingDocs))
                return elingDocs;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return null;
    }

    private static string? FindAncestorDocs(string start)
    {
        var dir = Path.GetFullPath(start);
        while (dir != null)
        {
            var docs = Path.Combine(dir, "docs");
            if (Directory.Exists(docs))
                return docs;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return null;
    }
}

/// <summary>
/// Result of resolving a document path to its scope properties.
/// </summary>
public sealed record DocScopeResolution(
    DocOwnership Ownership,
    DocOrigin Origin,
    string RelativePath,
    string RootPath);

/// <summary>
/// Minimal scope decision for index placement (reuses memory's decision enum).
/// </summary>
public sealed class DocScopeDecision
{
    public ScopeDecision Decision { get; set; } = ScopeDecision.Ask;
}

/// <summary>
/// Scope decision from memory (reused here).
/// </summary>
public enum ScopeDecision
{
    Ask = 0,
    Disabled = 1,
    Clear = 2,
}