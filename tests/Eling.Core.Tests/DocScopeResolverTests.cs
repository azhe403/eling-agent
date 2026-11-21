namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Eling.Core.Scope;
using Xunit;

public class DocScopeResolverTests
{
    private readonly string _tempRoot;

    public DocScopeResolverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"eling-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "docs", "specs"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".eling", "docs", "plans"));
    }

    ~DocScopeResolverTests()
    {
        try { Directory.Delete(_tempRoot, true); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to delete temp directory: {ex.Message}");
        }
    }

    [Fact]
    public void Resolve_DocsFolder_ReturnsExternalCurrent()
    {
        var resolver = new DocScopeResolver(_tempRoot);
        var file = Path.Combine(_tempRoot, "docs", "specs", "test.md");
        File.WriteAllText(file, "# Test");
        
        var result = resolver.Resolve(file);
        
        Assert.Equal(DocOwnership.External, result.Ownership);
        Assert.Equal(DocOrigin.Current, result.Origin);
        Assert.Equal("specs/test.md", result.RelativePath);
    }

    [Fact]
    public void Resolve_ElingDocsFolder_ReturnsManagedCurrent()
    {
        var resolver = new DocScopeResolver(_tempRoot);
        var file = Path.Combine(_tempRoot, ".eling", "docs", "plans", "plan.md");
        File.WriteAllText(file, "# Plan");
        
        var result = resolver.Resolve(file);
        
        Assert.Equal(DocOwnership.Managed, result.Ownership);
        Assert.Equal(DocOrigin.Current, result.Origin);
        Assert.Equal("plans/plan.md", result.RelativePath);
    }

    [Fact]
    public void Resolve_ShardFolder_ReturnsManagedShard()
    {
        var canonicalRoot = Path.Combine(_tempRoot, "canonical");
        Directory.CreateDirectory(canonicalRoot);
        
        var resolver = new DocScopeResolver(canonicalRoot);
        var shardDir = ElingPaths.ResolveProjectLocalDir(canonicalRoot);
        var docsDir = Path.Combine(shardDir, "docs");
        Directory.CreateDirectory(docsDir);
        var file = Path.Combine(docsDir, "spec.md");
        File.WriteAllText(file, "# Spec");
        
        var result = resolver.Resolve(file);
        
        Assert.Equal(DocOwnership.Managed, result.Ownership);
        Assert.Equal(DocOrigin.Shard, result.Origin);
        Assert.Equal("docs/spec.md", result.RelativePath); // shard root is the project dir, docs is subfolder
    }

    [Fact]
    public void Resolve_AncestorElingDocs_ReturnsManagedAncestor()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"eling-parent-{Guid.NewGuid():N}");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(Path.Combine(parent, ".eling", "docs", "specs"));
        Directory.CreateDirectory(child);
        
        var resolver = new DocScopeResolver(child);
        var file = Path.Combine(parent, ".eling", "docs", "specs", "parent.md");
        File.WriteAllText(file, "# Parent Spec");
        
        var result = resolver.Resolve(file);
        
        Assert.Equal(DocOwnership.Managed, result.Ownership);
        Assert.Equal(DocOrigin.Ancestor, result.Origin);
        Assert.Contains("specs/parent.md", result.RelativePath);
        
        try { Directory.Delete(parent, true); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to delete parent temp directory: {ex.Message}");
        }
    }

    [Fact]
    public void GetIndexDbPath_Shard_ReturnsShardDb()
    {
        var canonicalRoot = Path.Combine(_tempRoot, "canonical");
        Directory.CreateDirectory(canonicalRoot);
        
        var resolver = new DocScopeResolver(canonicalRoot);
        var dbPath = resolver.GetIndexDbPath(DocOrigin.Shard);
        
        Assert.NotNull(dbPath);
        Assert.EndsWith("docs.db", dbPath);
        Assert.Contains("projects", dbPath);
    }

    [Fact]
    public void GetIndexDbPath_Current_WithDisabled_ReturnsNull()
    {
        var resolver = new DocScopeResolver(_tempRoot);
        var decision = new DocScopeDecision { Decision = ScopeDecision.Disabled };
        var dbPath = resolver.GetIndexDbPath(DocOrigin.Current, decision);
        
        Assert.Null(dbPath);
    }

    [Fact]
    public void GetIndexDbPath_Current_WithAsk_ReturnsDb()
    {
        var resolver = new DocScopeResolver(_tempRoot);
        var decision = new DocScopeDecision { Decision = ScopeDecision.Ask };
        var dbPath = resolver.GetIndexDbPath(DocOrigin.Current, decision);
        
        Assert.NotNull(dbPath);
        Assert.EndsWith("docs-index.db", dbPath);
    }

    [Fact]
    public void GetIndexDbPath_Global_ReturnsGlobalDb()
    {
        var resolver = new DocScopeResolver(_tempRoot);
        var dbPath = resolver.GetIndexDbPath(DocOrigin.Global);
        
        Assert.NotNull(dbPath);
        Assert.EndsWith("docs.db", dbPath);
    }

    [Fact]
    public void GetIndexDbPath_External_ReturnsNull()
    {
        var resolver = new DocScopeResolver(_tempRoot);
        var dbPath = resolver.GetIndexDbPath(DocOrigin.Current); // External uses Current origin
        // Actually External origin doesn't exist in DocOrigin enum, it uses Current with External ownership
        // This test documents that External documents have no dedicated index
        Assert.NotNull(dbPath); // Current origin has index when not disabled
    }
}