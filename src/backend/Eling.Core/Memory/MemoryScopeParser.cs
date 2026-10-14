namespace Eling.Core.Memory;

public static class MemoryScopeParser
{
    // The string→kind mapping lives in one place (MemoryScopePolicy.Resolve);
    // this parser only adds its own contract: a blank scope is "no explicit
    // scope" (false), never the default.
    private static readonly MemoryScopePolicy Policy = new();

    public static bool TryParseScope(string? scope, out MemoryScopeKind kind)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            kind = default;
            return false;
        }

        return Policy.TryResolve(scope, out kind);
    }

    public static bool TryParseSearchScope(string? scope, out string normalized)
    {
        normalized = (scope ?? "merged").Trim().ToLowerInvariant();
        return normalized is "project" or "project-local" or "global" or "merged";
    }
}
