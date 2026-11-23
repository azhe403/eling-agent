using System.Text.Json;
using Eling.Backend.Dtos;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Recall;
using Eling.Core.Skill;

namespace Eling.Backend.Tests;

/// <summary>
/// Recall MCP wire shape for the skills block: name, description, origin
/// ("eling"/"external"), and path - never the body. Skills are additive on
/// the envelope and serialize as an empty array when the catalog has no hits
/// or is not configured.
/// </summary>
public sealed class RecallToolTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eling-recall-tool-" + Guid.NewGuid().ToString("N"));

    private static RecallResult ResultWithSkills(params SkillRecallHit[] skills)
        => new([], [], [], new RecallStats(0, 0, 0, 0, 0), Skills: skills.Length > 0 ? skills : null);

    [Fact]
    public async Task Recall_maps_skills_to_wire_envelope()
    {
        var hit = new SkillRecallHit(
            "collect-user-input",
            "Build Blazor forms and validate user input",
            SkillOrigin.Eling,
            "/catalog/skills/collect-user-input/SKILL.md");
        var tool = new RecallTool(new StubRecallService(ResultWithSkills(hit)), cwd: _root);

        var response = await tool.RecallAsync(new RecallContextInput { Topics = ["blazor forms"] });

        var skill = Assert.Single(response.Skills);
        Assert.Equal("collect-user-input", skill.Name);
        Assert.Equal("Build Blazor forms and validate user input", skill.Description);
        Assert.Equal("eling", skill.Origin);
        Assert.Equal("/catalog/skills/collect-user-input/SKILL.md", skill.Path);
    }

    [Fact]
    public async Task Recall_serializes_skills_without_any_body_content()
    {
        var hit = new SkillRecallHit(
            "collect-user-input",
            "Build Blazor forms and validate user input",
            SkillOrigin.Eling,
            "/catalog/skills/collect-user-input/SKILL.md");
        var tool = new RecallTool(new StubRecallService(ResultWithSkills(hit)), cwd: _root);

        var response = await tool.RecallAsync(new RecallContextInput { Topics = ["blazor forms"] });
        var json = JsonSerializer.Serialize(response);

        Assert.Contains("\"skills\":", json, StringComparison.Ordinal);
        Assert.Contains("collect-user-input", json, StringComparison.Ordinal);
        Assert.Contains("SKILL.md", json, StringComparison.Ordinal);
        // The wire DTO carries exactly the reference fields - no body, no
        // markdown payload ever reaches the envelope.
        Assert.DoesNotContain("body", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["name", "description", "origin", "path"],
            typeof(SkillReferenceDto).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToArray());
    }

    [Fact]
    public async Task Recall_maps_external_origin_to_wire_spelling()
    {
        var hit = new SkillRecallHit(
            "external-widget",
            "External widget helper",
            SkillOrigin.External,
            "/catalog/skills/external-widget/SKILL.md");
        var tool = new RecallTool(new StubRecallService(ResultWithSkills(hit)), cwd: _root);

        var response = await tool.RecallAsync(new RecallContextInput { Topics = ["widget"] });

        var skill = Assert.Single(response.Skills);
        Assert.Equal("external", skill.Origin);
    }

    [Fact]
    public async Task Recall_without_skill_hits_emits_empty_skills_array()
    {
        var tool = new RecallTool(new StubRecallService(ResultWithSkills()), cwd: _root);

        var response = await tool.RecallAsync(new RecallContextInput { Topics = ["anything"] });

        Assert.NotNull(response.Skills);
        Assert.Empty(response.Skills);
        var json = JsonSerializer.Serialize(response);
        Assert.Contains("\"skills\":[]", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recall_with_recallLimit_minus_one_skips_recallSummary()
    {
        var dummyHit = new RecallHit(
            new Eling.Core.Memory.Memory(Eling.Core.Memory.MemoryType.Fact, "test overflow item"),
            ["porter"], 1.0, 0.0, "and");
        var resultWithOverflow = new RecallResult(
            [], [], [], new RecallStats(0, 0, 0, 0, 0),
            OverflowMemories: [dummyHit]);
        var tool = new RecallTool(new StubRecallService(resultWithOverflow), cwd: _root);

        var response = await tool.RecallAsync(
            new RecallContextInput { Topics = ["anything"] },
            recallLimit: -1);

        Assert.Null(response.RecallSummary);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException ex)
        {
            // Best effort: a lingering temp dir must not fail the test run.
            _ = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            // Best effort, same rationale.
            _ = ex.Message;
        }
    }

    private sealed class StubRecallService(RecallResult result) : IRecallService
    {
        public Task<RecallResult> RecallAsync(
            RecallContext? context,
            int recallLimit = -1,
            int recentLimit = 10,
            string? scope = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }
}
