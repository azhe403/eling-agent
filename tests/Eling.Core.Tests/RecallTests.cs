using System.Text.Json;
using Eling.Core.Memory;
using Eling.Core.Memory.Serialization;
using Eling.Core.Recall;
using Eling.Core.Scope;
using Eling.Core.Skill;
using Eling.Core.Skill.Storage;

namespace Eling.Core.Tests;

/// <summary>
/// Recall augments memory recall with a reference-only skills block:
/// name, one-line description, origin, and path - never the body
/// (context inflation). The catalog is origin-agnostic; when no skill
/// service is configured, recall stays memory-only and Skills is null.
/// </summary>
public sealed class RecallTests : IDisposable
{
    private const string ChildRoot = @"C:\work\acme\integrations\payments";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eling-recall-skills-" + Guid.NewGuid().ToString("N"));

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "eling-recall-skills-" + Guid.NewGuid().ToString("N") + ".db");

    private sealed class FakeRecallMemoryService : IMemoryService
    {
        private readonly Dictionary<MemoryId, Memory.Memory> _items = new();

        public Task<MemorySaveResult> SaveAsync(Memory.Memory memory)
        {
            _items[memory.Id] = memory;
            return Task.FromResult(new MemorySaveResult(memory, SaveAction.Created));
        }

        public Task<Memory.Memory?> GetByIdAsync(MemoryId id)
            => Task.FromResult(_items.TryGetValue(id, out var m) ? m : null);

        public Task<Memory.Memory?> UpdateAsync(MemoryId id, string? content = null, MemoryType? type = null, string[]? tags = null, string? source = null, MemoryStatus? status = null)
            => Task.FromResult(_items.TryGetValue(id, out var m) ? m : null);

        public Task<bool> DeleteAsync(MemoryId id)
            => Task.FromResult(_items.Remove(id));

        public Task<IReadOnlyCollection<Memory.Memory>> ListAllAsync()
            => Task.FromResult<IReadOnlyCollection<Memory.Memory>>(_items.Values.ToList());

        public Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query)
        {
            var rank = -1000.0;
            var results = _items.Values
                .Where(m => m.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(m => new MemorySearchResult(m.Id, rank += 1.0, MatchedVia: ["porter"]))
                .ToList();
            return Task.FromResult<IReadOnlyCollection<MemorySearchResult>>(results);
        }

        public Task RebuildIndexAsync() => Task.CompletedTask;
    }

    private sealed class EmptyIntentionStorage : IIntentionStorage
    {
        public Task SaveAsync(Intention.Intention intention) => Task.CompletedTask;
        public Task<Intention.Intention?> GetByIdAsync(MemoryId id) => Task.FromResult<Intention.Intention?>(null);
        public Task<bool> DeleteAsync(MemoryId id) => Task.FromResult(true);
        public Task<IReadOnlyCollection<Intention.Intention>> ListAllAsync()
            => Task.FromResult<IReadOnlyCollection<Intention.Intention>>(Array.Empty<Intention.Intention>());
    }

    private static ScopedMemoryService BuildScoped(FakeRecallMemoryService child, FakeRecallMemoryService global)
        => new(
            [new ProjectLevel(new ProjectScope(ChildRoot), child)],
            global,
            new MemoryScopePolicy(),
            new MemoryMerger(),
            ChildRoot);

    private SkillService NewSkillService(ISkillIndex? index = null)
        => new(new FileSystemSkillStorage(_root), index ?? new SqliteSkillIndex(_dbPath));

    [Fact]
    public async Task Recall_includes_matching_skills_by_name_and_description()
    {
        var child = new FakeRecallMemoryService();
        var global = new FakeRecallMemoryService();
        // Memory index seeded with unrelated content: the skill hit must
        // surface on its own merits, not because a memory pointed at it.
        await global.SaveAsync(new Memory.Memory(MemoryType.Fact, "an unrelated note about keyboard layouts"));
        var skills = NewSkillService();
        await skills.SaveAsync(new SkillSaveInput(
            "collect-user-input",
            "Build Blazor forms and validate user input",
            "# body\n\nSECRET-SKILL-BODY-MARKER steps"));

        var service = new RecallService(BuildScoped(child, global), new EmptyIntentionStorage(), skills: skills);
        var result = await service.RecallAsync(new RecallContext(["blazor forms"], null, null));

        var skill = Assert.Single(result.Skills!);
        Assert.Equal("collect-user-input", skill.Name);
        Assert.Equal("Build Blazor forms and validate user input", skill.Description);
        Assert.Equal(SkillOrigin.Eling, skill.Origin);
        Assert.EndsWith(Path.Combine("collect-user-input", "SKILL.md"), skill.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recall_does_not_inline_skill_bodies()
    {
        var child = new FakeRecallMemoryService();
        var global = new FakeRecallMemoryService();
        var skills = NewSkillService();
        await skills.SaveAsync(new SkillSaveInput(
            "collect-user-input",
            "Build Blazor forms and validate user input",
            "# body\n\nSECRET-SKILL-BODY-MARKER steps"));

        var service = new RecallService(BuildScoped(child, global), new EmptyIntentionStorage(), skills: skills);
        var result = await service.RecallAsync(new RecallContext(["blazor forms"], null, null));

        Assert.Single(result.Skills!);
        var json = JsonSerializer.Serialize(result.Skills);
        Assert.DoesNotContain("SECRET-SKILL-BODY-MARKER", json, StringComparison.Ordinal);
        Assert.Contains("collect-user-input", json, StringComparison.Ordinal);
        Assert.Contains("SKILL.md", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recall_returns_skills_even_when_memory_index_empty()
    {
        var child = new FakeRecallMemoryService();
        var global = new FakeRecallMemoryService();
        var skills = NewSkillService();
        await skills.SaveAsync(new SkillSaveInput(
            "collect-user-input",
            "Build Blazor forms and validate user input",
            "# body\n\nsteps"));

        var service = new RecallService(BuildScoped(child, global), new EmptyIntentionStorage(), skills: skills);
        var result = await service.RecallAsync(new RecallContext(["blazor forms"], null, null));

        Assert.Empty(result.RecallMemories);
        var skill = Assert.Single(result.Skills!);
        Assert.Equal("collect-user-input", skill.Name);
    }

    [Fact]
    public async Task Recall_caps_skills_at_standing_baseline_limit()
    {
        var child = new FakeRecallMemoryService();
        var global = new FakeRecallMemoryService();
        var skills = NewSkillService();
        for (var i = 1; i <= 7; i++)
        {
            await skills.SaveAsync(new SkillSaveInput(
                $"widget-helper-{i:00}",
                "Handles widget rendering duties",
                "# body\n\nwidget rendering steps"));
        }

        var service = new RecallService(BuildScoped(child, global), new EmptyIntentionStorage(), skills: skills);
        var result = await service.RecallAsync(new RecallContext(["widget"], null, null));

        Assert.Equal(5, result.Skills!.Count);
    }

    [Fact]
    public async Task Recall_surfaces_external_skills()
    {
        var child = new FakeRecallMemoryService();
        var global = new FakeRecallMemoryService();
        var index = new SqliteSkillIndex(_dbPath);
        await index.AdoptExternalScanResultAsync(new Skill.Skill(
            "external-widget",
            "External widget helper",
            "widget helper body EXTERNAL-SKILL-BODY-MARKER",
            origin: SkillOrigin.External));
        var skills = NewSkillService(index);

        var service = new RecallService(BuildScoped(child, global), new EmptyIntentionStorage(), skills: skills);
        var result = await service.RecallAsync(new RecallContext(["widget"], null, null));

        var skill = Assert.Single(result.Skills!);
        Assert.Equal("external-widget", skill.Name);
        Assert.Equal(SkillOrigin.External, skill.Origin);
    }

    [Fact]
    public async Task Recall_without_skill_service_works_unchanged()
    {
        var child = new FakeRecallMemoryService();
        var global = new FakeRecallMemoryService();
        await child.SaveAsync(new Memory.Memory(MemoryType.Fact, "topic memory"));

        var service = new RecallService(BuildScoped(child, global), new EmptyIntentionStorage());
        var result = await service.RecallAsync(new RecallContext(["topic"], null, null));

        // Memory-only harness: no skills block, no failure, memory recall intact.
        Assert.Null(result.Skills);
        Assert.Single(result.RecallMemories);
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
            // Best effort: a lingering temp catalog must not fail the test run on Windows file locks.
            _ = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            // Best effort, same rationale.
            _ = ex.Message;
        }

        foreach (var candidate in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
            catch (IOException ex)
            {
                // Best effort, same rationale as the catalog cleanup above.
                _ = ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                // Best effort, same rationale.
                _ = ex.Message;
            }
        }
    }
}
