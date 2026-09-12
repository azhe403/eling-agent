namespace Eling.Core.Memory;

public interface IMemoryScopePolicy
{
    MemoryScopeKind Resolve(string? scope);
    bool TryResolve(string? scope, out MemoryScopeKind kind);
    MemoryScopeKind ResolveSearchScope(string? scope);
}

