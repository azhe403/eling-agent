using Eling.Core.Scope;

namespace Eling.Core.Tests;

public sealed class ProjectScopePolicyTests
{
    private static string Root(string relative)
        => ProjectScopePolicy.NormalizeRoot(Path.Combine(Path.GetTempPath(), "eling-policy-tests", relative));

    private static ProjectScopeEntry Entry(ProjectScopeDecision decision)
        => new(decision, null, null);

    private static ProjectScopePolicy Policy(
        ProjectScopeDecision fallback,
        (string Root, ProjectScopeDecision Decision)[]? projects = null,
        (string Glob, ProjectScopeDecision Decision)[]? patterns = null)
    {
        var dictionary = new Dictionary<string, ProjectScopeEntry>(StringComparer.OrdinalIgnoreCase);
        if (projects is not null)
        {
            foreach (var (root, decision) in projects)
            {
                dictionary[root] = Entry(decision);
            }
        }

        var list = new List<ProjectScopePattern>();
        if (patterns is not null)
        {
            foreach (var (glob, decision) in patterns)
            {
                list.Add(new ProjectScopePattern(glob, Entry(decision)));
            }
        }

        return new ProjectScopePolicy(null, null, fallback, dictionary, list);
    }

    [Fact]
    public void Resolve_EmptyPolicy_DefaultsToAsk()
    {
        Assert.Equal(ProjectScopeDecision.Ask, ProjectScopePolicy.DefaultPolicy.Resolve(Root("anything")));
    }

    [Fact]
    public void Resolve_ExactKey_WinsOverPattern()
    {
        var target = Root("acme/legacy-app");
        var policy = Policy(
            ProjectScopeDecision.Ask,
            projects: [(target, ProjectScopeDecision.Disabled)],
            patterns: [(Root("acme") + "/**", ProjectScopeDecision.Ask)]);

        Assert.Equal(ProjectScopeDecision.Disabled, policy.Resolve(target));
    }

    [Fact]
    public void Resolve_LongestGlob_Wins()
    {
        var target = Root("acme/legacy/app");
        var policy = Policy(
            ProjectScopeDecision.Ask,
            patterns:
            [
                (Root("acme") + "/**", ProjectScopeDecision.Ask),
                (Root("acme/legacy") + "/**", ProjectScopeDecision.Disabled)
            ]);

        Assert.Equal(ProjectScopeDecision.Disabled, policy.Resolve(target));
    }

    [Fact]
    public void Resolve_NoMatch_FallsBackToDefault()
    {
        var policy = Policy(
            ProjectScopeDecision.Disabled,
            patterns: [(Root("other") + "/**", ProjectScopeDecision.Ask)]);

        Assert.Equal(ProjectScopeDecision.Disabled, policy.Resolve(Root("acme/app")));
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var stored = Root("acme/legacy-app");
        var policy = Policy(ProjectScopeDecision.Ask, projects: [(stored, ProjectScopeDecision.Disabled)]);

        Assert.Equal(ProjectScopeDecision.Disabled, policy.Resolve(stored.ToUpperInvariant()));
    }

    [Fact]
    public void ResolveDetailed_Exact_ReportsProjectLevelWithEntry()
    {
        var target = Root("acme/app");
        var policy = Policy(ProjectScopeDecision.Ask, projects: [(target, ProjectScopeDecision.Disabled)]);

        var resolution = policy.ResolveDetailed(target);

        Assert.Equal(ProjectScopeDecision.Disabled, resolution.Decision);
        Assert.Equal(ProjectScopeResolution.Level.Project, resolution.MatchedLevel);
        Assert.NotNull(resolution.Entry);
    }

    [Fact]
    public void ResolveDetailed_Pattern_ReportsPatternLevel()
    {
        var target = Root("acme/app");
        var policy = Policy(ProjectScopeDecision.Ask, patterns: [(Root("acme") + "/**", ProjectScopeDecision.Disabled)]);

        var resolution = policy.ResolveDetailed(target);

        Assert.Equal(ProjectScopeDecision.Disabled, resolution.Decision);
        Assert.Equal(ProjectScopeResolution.Level.Pattern, resolution.MatchedLevel);
    }

    [Fact]
    public void ResolveDetailed_Default_ReportsDefaultLevelWithoutEntry()
    {
        var resolution = ProjectScopePolicy.DefaultPolicy.ResolveDetailed(Root("acme/app"));

        Assert.Equal(ProjectScopeResolution.Level.Default, resolution.MatchedLevel);
        Assert.Null(resolution.Entry);
    }
}
