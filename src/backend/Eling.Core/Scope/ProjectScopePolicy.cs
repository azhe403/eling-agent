using Eling.Core.Matching;

namespace Eling.Core.Scope;

/// <summary>
/// Machine-local project-scope policy. Pure: it resolves a project root to a
/// decision by specificity (exact root → longest matching glob → default) and
/// never touches disk. Timestamps are informational and never affect resolution.
/// </summary>
public sealed record ProjectScopePolicy(
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    ProjectScopeDecision Default,
    IReadOnlyDictionary<string, ProjectScopeEntry> Projects,
    IReadOnlyList<ProjectScopePattern> Patterns)
{
    /// <summary>The empty policy: every workspace is <c>ask</c>, with no dates.</summary>
    public static ProjectScopePolicy DefaultPolicy { get; } =
        new(null, null, ProjectScopeDecision.Ask,
            new Dictionary<string, ProjectScopeEntry>(), []);

    /// <summary>Resolve a project root to its decision (§5).</summary>
    public ProjectScopeDecision Resolve(string projectRoot) => ResolveDetailed(projectRoot).Decision;

    /// <summary>Resolve a project root, reporting which level won and its entry.</summary>
    public ProjectScopeResolution ResolveDetailed(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var normalized = NormalizeRoot(projectRoot);

        var exact = FindProject(normalized);
        if (exact is not null)
        {
            return new ProjectScopeResolution(
                exact.Decision, ProjectScopeResolution.Level.Project, exact);
        }

        ProjectScopePattern? best = null;
        foreach (var pattern in Patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern.Glob))
            {
                continue;
            }

            if (!GlobPattern.IsMatch(pattern.Glob, normalized))
            {
                continue;
            }

            if (best is null || pattern.Glob.Length > best.Glob.Length)
            {
                best = pattern;
            }
        }

        return best is not null
            ? new ProjectScopeResolution(
                best.Entry.Decision, ProjectScopeResolution.Level.Pattern, best.Entry)
            : new ProjectScopeResolution(Default, ProjectScopeResolution.Level.Default, null);
    }

    /// <summary>Absolute, forward-slash normalized project root used as a policy key.</summary>
    public static string NormalizeRoot(string path)
        => Path.GetFullPath(path).Replace('\\', '/');

    private ProjectScopeEntry? FindProject(string normalizedRoot)
    {
        if (Projects.TryGetValue(normalizedRoot, out var found))
        {
            return found;
        }

        // Tolerate a case-sensitive dictionary (e.g. a hand-built test policy) while
        // keeping matching case-insensitive per the design.
        foreach (var pair in Projects)
        {
            if (string.Equals(pair.Key, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }
}
