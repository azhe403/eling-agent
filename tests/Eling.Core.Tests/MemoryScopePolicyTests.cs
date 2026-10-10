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
    public void Resolve_Defaults_Unchanged()
    {
        Assert.Equal(MemoryScopeKind.Project, _policy.Resolve(null));
        Assert.Equal(MemoryScopeKind.Project, _policy.Resolve("auto"));
        Assert.Equal(MemoryScopeKind.Project, _policy.Resolve("project"));
        Assert.Equal(MemoryScopeKind.Global, _policy.Resolve("global"));
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
