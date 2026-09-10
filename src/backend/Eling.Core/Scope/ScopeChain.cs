namespace Eling.Core;

/// <summary>
/// Ordered chain of project scopes discovered from a working directory up to
/// the nearest initialized ancestor, nearest first. Level 0 is the write
/// target; an empty chain means the project scope is uninitialized.
/// </summary>
public sealed record ScopeChain(string Cwd, IReadOnlyList<ProjectScope> Levels)
{
    public ProjectScope? Head => Levels.Count > 0 ? Levels[0] : null;

    public bool IsInitialized => Head is not null;

    public bool HasOwnScope =>
        Levels.Count > 0 &&
        string.Equals(
            Levels[0].Root.TrimEnd(Path.DirectorySeparatorChar),
            Cwd.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <param name="stopAtDirectory">
    /// Optional ceiling for the upward walk (test seam); production callers omit it.
    /// </param>
    public static ScopeChain Discover(string? cwd = null, string? stopAtDirectory = null)
    {
        var start = string.IsNullOrWhiteSpace(cwd)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(cwd);
        return new ScopeChain(start, ProjectScope.DiscoverChain(start, stopAtDirectory));
    }
}