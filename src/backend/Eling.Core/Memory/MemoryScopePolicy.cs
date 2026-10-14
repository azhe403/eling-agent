namespace Eling.Core.Memory;

public sealed class MemoryScopePolicy : IMemoryScopePolicy
{
    public MemoryScopeKind Resolve(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            // No scope specified means the default — and the default is
            // project-local: saves stay machine-only unless sharing is
            // explicitly requested. (Blank used to resolve to project, which
            // quietly pulled agent saves toward the committed tier.)
            return MemoryScopeKind.ProjectLocal;
        }

        var normalized = scope.Trim().ToLowerInvariant();
        return normalized switch
        {
            "project" => MemoryScopeKind.Project,
            "project-local" => MemoryScopeKind.ProjectLocal,
            "global" => MemoryScopeKind.Global,
            // 'auto' survives only as a tolerated alias for "no preference"
            // left over from the old string contract; it resolves to the
            // default, which is now project-local.
            "auto" => MemoryScopeKind.ProjectLocal,
            _ => throw new ArgumentException($"Invalid scope '{scope}'. Valid: project, project-local, global, auto", nameof(scope))
        };
    }

    public bool TryResolve(string? scope, out MemoryScopeKind kind)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            kind = MemoryScopeKind.ProjectLocal;
            return true;
        }

        // Delegates to Resolve so the string→kind mapping lives in exactly one
        // switch (the parser and the tool boundary share that same map).
        try
        {
            kind = Resolve(scope);
            return true;
        }
        catch (ArgumentException)
        {
            kind = default;
            return false;
        }
    }

    public MemoryScopeKind ResolveSearchScope(string? scope)
    {
        throw new NotSupportedException("Use TryParseSearchScope for search scopes (project|global|merged)");
    }
}
