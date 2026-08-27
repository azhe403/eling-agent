using Eling.Core;

namespace Eling.Application;

public interface IMemoryScopePolicy
{
    MemoryScopeKind Resolve(string? scope);
    bool TryResolve(string? scope, out MemoryScopeKind kind);
    MemoryScopeKind ResolveSearchScope(string? scope);
}

public static class MemoryScopeParser
{
    public static bool TryParseScope(string? scope, out MemoryScopeKind kind)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            kind = default;
            return false;
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

    public static bool TryParseSearchScope(string? scope, out string normalized)
    {
        normalized = (scope ?? "merged").Trim().ToLowerInvariant();
        return normalized is "project" or "global" or "merged";
    }
}
