using Eling.Backend.Scope;
using Eling.Core.Scope;

namespace Eling.Backend.Tests;

public sealed class JsonProjectScopePolicyStoreTests : IDisposable
{
    private readonly string _root;
    private DateTimeOffset _now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    public JsonProjectScopePolicyStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-policy-store-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private JsonProjectScopePolicyStore NewStore(string? home = null)
        => new(new UserScope(_root), () => _now, home ?? Path.Combine(_root, "home"));

    private string PolicyPath => Path.Combine(_root, "config", "project-policy.json");

    private string ProjectRoot(string name) => Path.Combine(_root, name);

    [Fact]
    public async Task Load_MissingFile_ReturnsDefaultAsk()
    {
        var policy = await NewStore().LoadAsync();

        Assert.Equal(ProjectScopeDecision.Ask, policy.Default);
        Assert.Empty(policy.Projects);
        Assert.Null(policy.CreatedAt);
    }

    [Fact]
    public async Task Load_CorruptFile_ReturnsDefaultWithoutThrowingOrOverwriting()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PolicyPath)!);
        File.WriteAllText(PolicyPath, "{ this is not json");

        var policy = await NewStore().LoadAsync();

        Assert.Equal(ProjectScopeDecision.Ask, policy.Default);
        Assert.Equal("{ this is not json", File.ReadAllText(PolicyPath));
    }

    [Fact]
    public async Task SetProject_WritesEntryAndDocumentTimestamps()
    {
        var store = NewStore();
        var project = ProjectRoot("acme");

        var policy = await store.SetProjectAsync(project, ProjectScopeDecision.Disabled);

        Assert.True(File.Exists(PolicyPath));
        Assert.Equal(_now, policy.CreatedAt);
        Assert.Equal(_now, policy.UpdatedAt);

        var key = ProjectScopePolicy.NormalizeRoot(project);
        Assert.True(policy.Projects.TryGetValue(key, out var entry));
        Assert.Equal(ProjectScopeDecision.Disabled, entry!.Decision);
        Assert.Equal(_now, entry.CreatedAt);
        Assert.Equal(_now, entry.UpdatedAt);
    }

    [Fact]
    public async Task SetProject_Idempotent_DoesNotBumpOrRewrite()
    {
        var store = NewStore();
        var project = ProjectRoot("acme");
        await store.SetProjectAsync(project, ProjectScopeDecision.Disabled);
        var firstWrite = File.ReadAllText(PolicyPath);

        _now = _now.AddMinutes(10);
        var policy = await store.SetProjectAsync(project, ProjectScopeDecision.Disabled);

        var key = ProjectScopePolicy.NormalizeRoot(project);
        Assert.True(policy.Projects.TryGetValue(key, out var entry));
        Assert.Equal(firstWrite, File.ReadAllText(PolicyPath));
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero), entry!.UpdatedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero), policy.UpdatedAt);
    }

    [Fact]
    public async Task SetProject_ChangeDecision_PreservesCreatedAtAndBumpsUpdatedAt()
    {
        var store = NewStore();
        var project = ProjectRoot("acme");
        var created = _now;
        await store.SetProjectAsync(project, ProjectScopeDecision.Disabled);

        _now = _now.AddHours(1);
        var policy = await store.SetProjectAsync(project, ProjectScopeDecision.Ask);

        var key = ProjectScopePolicy.NormalizeRoot(project);
        Assert.True(policy.Projects.TryGetValue(key, out var entry));
        Assert.Equal(ProjectScopeDecision.Ask, entry!.Decision);
        Assert.Equal(created, entry.CreatedAt);
        Assert.Equal(created, policy.CreatedAt);
        Assert.Equal(_now, entry.UpdatedAt);
        Assert.Equal(_now, policy.UpdatedAt);
    }

    [Fact]
    public async Task ClearProject_RemovesEntryAndBumpsDocument()
    {
        var store = NewStore();
        var project = ProjectRoot("acme");
        var created = _now;
        await store.SetProjectAsync(project, ProjectScopeDecision.Disabled);

        _now = _now.AddHours(2);
        var policy = await store.ClearProjectAsync(project);

        Assert.Empty(policy.Projects);
        Assert.Equal(created, policy.CreatedAt);
        Assert.Equal(_now, policy.UpdatedAt);
    }

    [Fact]
    public async Task ClearProject_Missing_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => NewStore().ClearProjectAsync(ProjectRoot("nope")));
    }

    [Fact]
    public async Task SetPattern_ExpandsHomeAndResolves()
    {
        var home = Path.Combine(_root, "home");
        var store = NewStore(home);

        var policy = await store.SetPatternAsync("~/work/acme/legacy/**", ProjectScopeDecision.Disabled);

        var pattern = Assert.Single(policy.Patterns);
        Assert.Equal(ProjectScopePolicy.NormalizeRoot(home) + "/work/acme/legacy/**", pattern.Glob);
        Assert.Equal(
            ProjectScopeDecision.Disabled,
            policy.Resolve(Path.Combine(home, "work", "acme", "legacy", "app")));
    }

    [Fact]
    public async Task SetDefault_ChangesFallback()
    {
        var policy = await NewStore().SetDefaultAsync(ProjectScopeDecision.Disabled);

        Assert.Equal(ProjectScopeDecision.Disabled, policy.Default);
        Assert.Equal(ProjectScopeDecision.Disabled, policy.Resolve(ProjectRoot("anything")));
    }

    [Fact]
    public async Task Save_PreservesVersionAndUnknownKeys()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PolicyPath)!);
        File.WriteAllText(PolicyPath, "{\n  \"version\": 7,\n  \"customKey\": \"keep-me\"\n}");

        await NewStore().SetProjectAsync(ProjectRoot("acme"), ProjectScopeDecision.Disabled);

        var json = File.ReadAllText(PolicyPath);
        Assert.Contains("\"version\": 7", json);
        Assert.Contains("keep-me", json);
    }
}
