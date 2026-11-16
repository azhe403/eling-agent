using Eling.Core.Skill;
using Eling.Core.Skill.Storage;
using Xunit;

namespace Eling.Core.Tests;

public sealed class SkillServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eling-skill-service-" + Guid.NewGuid().ToString("N"));

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "eling-skill-service-" + Guid.NewGuid().ToString("N") + ".db");

    private string SkillFolder(string name) => Path.Combine(_root, "skills", name);
    private string SkillFile(string name) => Path.Combine(SkillFolder(name), "SKILL.md");
    private string TrashFolder(string name) => Path.Combine(_root, ".trash", "skills", name);

    private SkillService NewService(ISkillIndex? index = null, ISkillChangeNotifier? notifier = null) =>
        new(new FileSystemSkillStorage(_root), index ?? new SqliteSkillIndex(_dbPath), notifier);

    private static SkillSaveInput NewInput(string name, string description = "does useful things", string body = "# body\n\nsteps") =>
        new(name, description, body);

    private static string NameOf(SkillSearchResult hit) =>
        Path.GetFileName(Path.GetDirectoryName(hit.Path)!)!;

    [Fact]
    public async Task Save_creates_new_skill_in_folder_and_index()
    {
        var service = NewService();

        var saved = await service.SaveAsync(NewInput("my-skill"));

        Assert.True(File.Exists(SkillFile("my-skill")));
        Assert.Equal(SkillOrigin.Eling, saved.Origin);

        var fetched = await service.GetByNameAsync("my-skill");
        Assert.NotNull(fetched);
        Assert.Equal(saved.Id, fetched!.Id);
        Assert.Equal("my-skill", fetched.Name);

        var (items, total, matching) = await service.ListAsync(includeTrashed: false, origin: null);
        Assert.Equal(1, total);
        Assert.Equal(1, matching);
        Assert.Single(items);
    }

    [Fact]
    public async Task Save_update_preserves_id_and_createdAt()
    {
        var service = NewService();
        var created = await service.SaveAsync(NewInput("my-skill", "first description", "# first"));

        var updated = await service.SaveAsync(NewInput("my-skill", "second description", "# second"));

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);

        var fetched = await service.GetByNameAsync("my-skill");
        Assert.Equal(created.Id, fetched!.Id);
        Assert.Equal("second description", fetched.Description);
        Assert.Equal("# second", fetched.Body.TrimEnd());
    }

    [Fact]
    public async Task Save_preserves_source_across_update()
    {
        // Source is not part of the save input; it travels on the aggregate.
        // Seed a skill file that carries it (and its index row) directly, then
        // update through the service: the field must survive the rewrite.
        var index = new SqliteSkillIndex(_dbPath);
        var storage = new FileSystemSkillStorage(_root);
        var sourced = new Skill.Skill(
            "sourced-skill",
            "does things",
            "# body",
            source: new SkillSource("example.test/upstream", Tracked: true));
        await storage.SaveAsync(sourced);
        await index.IndexAsync(sourced);

        var service = NewService(index);
        var updated = await service.SaveAsync(new SkillSaveInput("sourced-skill", "rewritten", "# body two"));

        Assert.Equal("example.test/upstream", updated.Source?.Upstream);
        Assert.True(updated.Source?.Tracked);
        Assert.Equal("rewritten", updated.Description);
        Assert.Equal(sourced.Id, updated.Id);
    }

    [Fact]
    public async Task Save_rejects_external_name_collision()
    {
        var index = new SqliteSkillIndex(_dbPath);
        await index.AdoptExternalScanResultAsync(
            new Skill.Skill("my-skill", "external desc", "external body", origin: SkillOrigin.External));
        var service = NewService(index);

        await Assert.ThrowsAsync<SkillReadOnlyException>(() =>
            service.SaveAsync(NewInput("my-skill")));
    }

    [Fact]
    public async Task Save_rejects_kebab_invalid_name()
    {
        var service = NewService();

        await Assert.ThrowsAsync<SkillValidationException>(() => service.SaveAsync(NewInput("Bad_Name")));
        Assert.False(Directory.Exists(SkillFolder("Bad_Name")));
    }

    [Fact]
    public async Task Save_rejects_empty_body_and_oversized_description()
    {
        var service = NewService();

        await Assert.ThrowsAsync<SkillValidationException>(() =>
            service.SaveAsync(NewInput("ok-name", description: new string('x', Skill.Skill.MaxDescriptionLength + 1))));
        await Assert.ThrowsAsync<SkillValidationException>(() =>
            service.SaveAsync(NewInput("ok-name", body: "   ")));
    }

    [Fact]
    public async Task Save_over_trashed_name_throws_conflict()
    {
        var service = NewService();
        await service.SaveAsync(NewInput("my-skill"));
        await service.DeleteAsync("my-skill");

        await Assert.ThrowsAsync<SkillConflictException>(() => service.SaveAsync(NewInput("my-skill")));
    }

    [Fact]
    public async Task Rename_renames_folder_preserves_ulid()
    {
        var service = NewService();
        var created = await service.SaveAsync(NewInput("a-skill"));

        var renamed = await service.RenameAsync("a-skill", "b-skill");

        Assert.Equal(created.Id, renamed.Id);
        Assert.False(Directory.Exists(SkillFolder("a-skill")));
        Assert.True(Directory.Exists(SkillFolder("b-skill")));
        Assert.Null(await service.GetByNameAsync("a-skill"));
        var fetched = await service.GetByNameAsync("b-skill");
        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async Task Rename_refuses_target_held_by_external_skill()
    {
        var index = new SqliteSkillIndex(_dbPath);
        await index.AdoptExternalScanResultAsync(
            new Skill.Skill("b-skill", "external desc", "external body", origin: SkillOrigin.External));
        var service = NewService(index);
        await service.SaveAsync(NewInput("a-skill"));

        await Assert.ThrowsAsync<SkillReadOnlyException>(() => service.RenameAsync("a-skill", "b-skill"));
        Assert.True(Directory.Exists(SkillFolder("a-skill")));
    }

    [Fact]
    public async Task Delete_moves_to_trash_and_marks_index()
    {
        var service = NewService();
        await service.SaveAsync(NewInput("my-skill"));

        await service.DeleteAsync("my-skill");

        Assert.Null(await service.GetByNameAsync("my-skill"));
        Assert.False(Directory.Exists(SkillFolder("my-skill")));
        Assert.True(Directory.Exists(TrashFolder("my-skill")));

        var active = await service.ListAsync(includeTrashed: false, origin: null);
        Assert.Empty(active.Items);
        var everything = await service.ListAsync(includeTrashed: true, origin: null);
        Assert.Single(everything.Items);
        Assert.Equal("my-skill", everything.Items[0].Name);
        Assert.Equal(1, everything.Matching);
        Assert.Empty(await service.SearchAsync("body"));
    }

    [Fact]
    public async Task Restore_restores_from_trash()
    {
        var service = NewService();
        var created = await service.SaveAsync(NewInput("my-skill"));
        await service.DeleteAsync("my-skill");

        await service.RestoreAsync("my-skill");

        var restored = await service.GetByNameAsync("my-skill");
        Assert.NotNull(restored);
        Assert.Equal(created.Id, restored!.Id);
        Assert.True(Directory.Exists(SkillFolder("my-skill")));
        Assert.False(Directory.Exists(TrashFolder("my-skill")));

        // Row must be active again: visible with default includeTrashed=false.
        var (items, _, matching) = await service.ListAsync(includeTrashed: false, origin: null);
        Assert.Equal(1, matching);
        Assert.Single(items);
    }

    [Fact]
    public async Task Delete_refuses_external()
    {
        var index = new SqliteSkillIndex(_dbPath);
        await index.AdoptExternalScanResultAsync(
            new Skill.Skill("ext-skill", "external desc", "external body", origin: SkillOrigin.External));
        var service = NewService(index);

        await Assert.ThrowsAsync<SkillReadOnlyException>(() => service.DeleteAsync("ext-skill"));
        await Assert.ThrowsAsync<SkillNotFoundException>(() => service.EnsureElingOwnedAsync("absent-skill"));
    }

    [Fact]
    public async Task GetById_returns_active_skill_and_ignores_trashed()
    {
        var service = NewService();
        var created = await service.SaveAsync(NewInput("my-skill"));

        Assert.Equal(created.Id, (await service.GetByIdAsync(created.Id))!.Id);

        await service.DeleteAsync("my-skill");
        Assert.Null(await service.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task List_paginates_and_reports_total_and_matching()
    {
        var service = NewService();
        for (var i = 1; i <= 25; i++)
        {
            await service.SaveAsync(NewInput($"skill-{i:00}"));
        }

        var page2 = await service.ListAsync(includeTrashed: false, origin: "eling", page: 2, pageSize: 10, sort: "name");

        Assert.Equal(25, page2.Total);
        Assert.Equal(25, page2.Matching);
        Assert.Equal(10, page2.Items.Count);
        Assert.Equal("skill-11", page2.Items[0].Name);
    }

    [Fact]
    public async Task RebuildIndex_preserves_external_rows_and_drops_trashed()
    {
        var index = new SqliteSkillIndex(_dbPath);
        var externalFolder = SkillFolder("ext-keep");
        Directory.CreateDirectory(externalFolder);
        await File.WriteAllTextAsync(Path.Combine(externalFolder, "SKILL.md"), "# foreign skill, no eling frontmatter\n");
        await index.AdoptExternalScanResultAsync(
            new Skill.Skill("ext-keep", "external desc", "external body", origin: SkillOrigin.External));

        var service = NewService(index);
        var created = await service.SaveAsync(NewInput("my-skill"));
        await service.DeleteAsync("my-skill");

        await service.RebuildIndexAsync();

        // Trashed skills are dropped from the index by rebuild (the trash marker is
        // not part of the aggregate); restore still works and re-activates the row.
        var everything = await service.ListAsync(includeTrashed: true, origin: null);
        Assert.Single(everything.Items);
        Assert.Equal("ext-keep", everything.Items[0].Name);

        var external = await service.GetByNameAsync("ext-keep");
        Assert.NotNull(external);
        Assert.Equal(SkillOrigin.External, external!.Origin);

        await service.RestoreAsync("my-skill");
        var restored = await service.GetByNameAsync("my-skill");
        Assert.Equal(created.Id, restored!.Id);
    }

    [Fact]
    public async Task Search_excludes_trashed_rows()
    {
        var service = NewService();
        await service.SaveAsync(new SkillSaveInput("my-skill", "searchable", "uniqueterm here"));
        await service.DeleteAsync("my-skill");

        Assert.Empty(await service.SearchAsync("uniqueterm"));
    }

    [Fact]
    public async Task GetByName_returns_null_for_unknown_name()
    {
        var service = NewService();

        Assert.Null(await service.GetByNameAsync("never-saved"));
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
            // Same.
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
                // Same.
                _ = ex.Message;
            }
        }
    }
}
