using Eling.Core;

namespace Eling.Core;

public sealed class MemoryScopePolicy : IMemoryScopePolicy
{
    public MemoryScopeKind Resolve(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return MemoryScopeKind.Project;
        }

        var normalized = scope.Trim().ToLowerInvariant();
        return normalized switch
        {
            "project" => MemoryScopeKind.Project,
            "global" => MemoryScopeKind.Global,
            "auto" => MemoryScopeKind.Project,
            _ => throw new ArgumentException($"Invalid scope '{scope}'. Valid: project, global, auto", nameof(scope))
        };
    }

    public bool TryResolve(string? scope, out MemoryScopeKind kind)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            kind = MemoryScopeKind.Project;
            return true;
        }

        var normalized = scope.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "project":
                kind = MemoryScopeKind.Project;
                return true;
            case "global":
                kind = MemoryScopeKind.Global;
                return true;
            case "auto":
                kind = MemoryScopeKind.Project;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public MemoryScopeKind ResolveSearchScope(string? scope)
    {
        throw new NotSupportedException("Use TryParseSearchScope for search scopes (project|global|merged)");
    }
}

