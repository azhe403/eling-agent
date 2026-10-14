using Eling.Core.Memory;

namespace Eling.Core.Tests;

public sealed class MemoryScopePolicyTests
{
    private readonly MemoryScopePolicy _policy = new();

    [Fact]
    public void Resolve_ProjectLocal_Explicit()
    {
        Assert.Equal(MemoryScopeKind.ProjectLocal, _policy.Resolve("project-local"));
    }

    [Fact]
    public void Resolve_Defaults_ToProjectLocal()
    {
        // 2026-10-14: the default flipped from project to project-local —
        // blank and the 'auto' alias now resolve machine-local unless the
        // caller explicitly asks for a different tier.
        Assert.Equal(MemoryScopeKind.ProjectLocal, _policy.Resolve(null));
        Assert.Equal(MemoryScopeKind.ProjectLocal, _policy.Resolve("auto"));
        Assert.Equal(MemoryScopeKind.ProjectLocal, _policy.Resolve("   "));
        Assert.Equal(MemoryScopeKind.Project, _policy.Resolve("project"));
        Assert.Equal(MemoryScopeKind.Global, _policy.Resolve("global"));
    }

    [Fact]
    public void TryResolve_Defaults_ToProjectLocal()
    {
        Assert.True(_policy.TryResolve(null, out var blank));
        Assert.Equal(MemoryScopeKind.ProjectLocal, blank);
        Assert.True(_policy.TryResolve("auto", out var auto));
        Assert.Equal(MemoryScopeKind.ProjectLocal, auto);
    }

    [Fact]
    public void TryParseScope_AcceptsProjectLocal()
    {
        Assert.True(MemoryScopeParser.TryParseScope("project-local", out var kind));
        Assert.Equal(MemoryScopeKind.ProjectLocal, kind);
    }

    [Fact]
    public void TryParseSearchScope_AcceptsProjectLocal()
    {
        Assert.True(MemoryScopeParser.TryParseSearchScope("project-local", out var normalized));
        Assert.Equal("project-local", normalized);
    }
}
