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

        var saved = (await service.SaveAsync(NewInput("my-skill"))).Skill;

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
        var created = (await service.SaveAsync(NewInput("my-skill", "first description", "# first"))).Skill;

        var updated = (await service.SaveAsync(NewInput("my-skill", "second description", "# second"))).Skill;

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
        var updated = (await service.SaveAsync(new SkillSaveInput("sourced-skill", "rewritten", "# body two"))).Skill;

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
        var created = (await service.SaveAsync(NewInput("a-skill"))).Skill;

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
    public async Task Save_after_trash_folder_deleted_out_of_band_clears_trashed_marker()
    {
        var service = NewService();
        await service.SaveAsync(NewInput("drift-skill"));
        await service.DeleteAsync("drift-skill");

        // The trash folder is deleted out of band, so the normal save-guard
        // (trash occupies the name) does not fire; without the explicit clear,
        // the preserved trashed_at would resurrect the marker on a live save.
        Directory.Delete(TrashFolder("drift-skill"), recursive: true);

        await service.SaveAsync(NewInput("drift-skill"));

        var (items, _, _) = await service.ListAsync(includeTrashed: false, origin: null);
        Assert.Single(items);
        Assert.Equal("drift-skill", items.Single().Name);
    }

    [Fact]
    public async Task Rename_after_trash_folder_restored_out_of_band_clears_trashed_marker()
    {
        var service = NewService();
        var created = (await service.SaveAsync(NewInput("ren-me"))).Skill;
        await service.DeleteAsync("ren-me");

        // Trash folder put back to the active catalog out of band; the index
        // still carries the trashed_at marker (no restore call happened).
        Directory.Move(TrashFolder("ren-me"), SkillFolder("ren-me"));

        await service.RenameAsync("ren-me", "renamed-skill");

        var (items, _, _) = await service.ListAsync(includeTrashed: false, origin: null);
        Assert.Equal("renamed-skill", items.Single().Name);
        Assert.Equal(created.Id, items.Single().Id);
    }

    [Fact]
    public async Task Restore_restores_from_trash()
    {
        var service = NewService();
        var created = (await service.SaveAsync(NewInput("my-skill"))).Skill;
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
    public async Task Delete_external_moves_to_trash_and_marks_index()
    {
        var index = new SqliteSkillIndex(_dbPath);
        var externalFolder = SkillFolder("ext-skill");
        Directory.CreateDirectory(externalFolder);
        await File.WriteAllTextAsync(
            Path.Combine(externalFolder, "SKILL.md"),
            "# foreign skill, no eling frontmatter\n");
        await index.AdoptExternalScanResultAsync(
            new Skill.Skill("ext-skill", "external desc", "external body", origin: SkillOrigin.External));
        var service = NewService(index);

        await service.DeleteAsync("ext-skill");

        Assert.False(Directory.Exists(SkillFolder("ext-skill")));
        Assert.True(Directory.Exists(TrashFolder("ext-skill")));
        Assert.Null(await service.GetByNameAsync("ext-skill"));

        var active = await service.ListAsync(includeTrashed: false, origin: null);
        Assert.Empty(active.Items);
        var everything = await service.ListAsync(includeTrashed: true, origin: null);
        var trashed = Assert.Single(everything.Items);
        Assert.Equal("ext-skill", trashed.Name);
        Assert.Equal(SkillOrigin.External, trashed.Origin);
        Assert.Empty(await service.SearchAsync("external"));
    }

    [Fact]
    public async Task Restore_external_roundtrip_reactivates_index_row()
    {
        var index = new SqliteSkillIndex(_dbPath);
        var externalFolder = SkillFolder("ext-skill");
        Directory.CreateDirectory(externalFolder);
        await File.WriteAllTextAsync(
            Path.Combine(externalFolder, "SKILL.md"),
            "# foreign skill, no eling frontmatter\n");
        await index.AdoptExternalScanResultAsync(
            new Skill.Skill("ext-skill", "external desc", "external body", origin: SkillOrigin.External));
        var service = NewService(index);
        var adopted = await service.GetByNameAsync("ext-skill");

        await service.DeleteAsync("ext-skill");
        await service.RestoreAsync("ext-skill");

        Assert.True(Directory.Exists(SkillFolder("ext-skill")));
        Assert.False(Directory.Exists(TrashFolder("ext-skill")));

        var restored = await service.GetByNameAsync("ext-skill");
        Assert.NotNull(restored);
        Assert.Equal(SkillOrigin.External, restored!.Origin);
        Assert.Equal(adopted!.Id, restored.Id);

        var (items, _, matching) = await service.ListAsync(includeTrashed: false, origin: null);
        Assert.Equal(1, matching);
        Assert.Single(items);
    }

    [Fact]
    public async Task Trashed_external_name_stays_blocked_for_reuse()
    {
        var index = new SqliteSkillIndex(_dbPath);
        var externalFolder = SkillFolder("ext-skill");
        Directory.CreateDirectory(externalFolder);
        await File.WriteAllTextAsync(
            Path.Combine(externalFolder, "SKILL.md"),
            "# foreign skill, no eling frontmatter\n");
        await index.AdoptExternalScanResultAsync(
            new Skill.Skill("ext-skill", "external desc", "external body", origin: SkillOrigin.External));
        var service = NewService(index);
        await service.DeleteAsync("ext-skill");

        // The name stays occupied while trashed: a save under it is refused
        // (the trashed row is externally owned) rather than clobbering it.
        await Assert.ThrowsAsync<SkillReadOnlyException>(() => service.SaveAsync(NewInput("ext-skill")));
        await Assert.ThrowsAsync<SkillNotFoundException>(() => service.EnsureElingOwnedAsync("absent-skill"));
    }

    [Fact]
    public async Task Edit_external_still_throws_after_delete_allowed()
    {
        var index = new SqliteSkillIndex(_dbPath);
        var externalFolder = SkillFolder("ext-skill");
        Directory.CreateDirectory(externalFolder);
        await File.WriteAllTextAsync(
            Path.Combine(externalFolder, "SKILL.md"),
            "# foreign skill, no eling frontmatter\n");
        await index.AdoptExternalScanResultAsync(
            new Skill.Skill("ext-skill", "external desc", "external body", origin: SkillOrigin.External));
        var service = NewService(index);

        await Assert.ThrowsAsync<SkillReadOnlyException>(() => service.RenameAsync("ext-skill", "renamed-skill"));
        await Assert.ThrowsAsync<SkillReadOnlyException>(() => service.SaveAsync(NewInput("ext-skill")));
    }

    [Fact]
    public async Task GetById_returns_active_skill_and_ignores_trashed()
    {
        var service = NewService();
        var created = (await service.SaveAsync(NewInput("my-skill"))).Skill;

        Assert.Equal(created.Id, (await service.GetByIdAsync(created.Id))!.Id);

        await service.DeleteAsync("my-skill");
        Assert.Null(await service.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task List_total_is_selection_size_matching_is_post_filter()
    {
        // Mixture: 3 active eling skills, 1 of which is trashed; 1 missing-folder
        // row (folder deleted out of band — the index row remains); 1 external row
        // adopted without a folder (index mirror only).
        var index = new SqliteSkillIndex(_dbPath);
        var service = NewService(index);
        await service.SaveAsync(NewInput("alpha-skill"));
        await service.SaveAsync(NewInput("beta-skill"));
        await service.SaveAsync(NewInput("gamma-skill"));
        await service.SaveAsync(NewInput("missing-skill"));
        Directory.Delete(SkillFolder("missing-skill"), recursive: true);
        await index.AdoptExternalScanResultAsync(
            new Skill.Skill("external-skill", "external desc", "external body", origin: SkillOrigin.External));
        await service.DeleteAsync("beta-skill");

        // Default (active-only) selection: 3 eling + external - trashed = 4 rows.
        var active = await service.ListAsync(includeTrashed: false, origin: null);
        Assert.Equal(4, active.Total);
        Assert.Equal(4, active.Matching);
        Assert.Equal(4, active.Items.Count);

        // Trash mode widens the selection by exactly the trashed row.
        var withTrash = await service.ListAsync(includeTrashed: true, origin: null);
        Assert.Equal(5, withTrash.Total);
        Assert.Equal(5, withTrash.Matching);

        // Origin filter narrows Matching only; Total stays the selection size.
        var elingOnly = await service.ListAsync(includeTrashed: false, origin: "eling");
        Assert.Equal(4, elingOnly.Total);
        Assert.Equal(3, elingOnly.Matching);

        var externalOnly = await service.ListAsync(includeTrashed: false, origin: "external");
        Assert.Equal(4, externalOnly.Total);
        Assert.Equal(1, externalOnly.Matching);

        // Paging still windows the selection; Total/Matching do not shrink.
        var page2 = await service.ListAsync(includeTrashed: false, origin: null, page: 2, pageSize: 3);
        Assert.Equal(4, page2.Total);
        Assert.Equal(4, page2.Matching);
        Assert.Single(page2.Items);

        // Trash mode with the origin filter: selection includes trashed rows.
        var elingTrashed = await service.ListAsync(includeTrashed: true, origin: "eling");
        Assert.Equal(5, elingTrashed.Total);
        Assert.Equal(4, elingTrashed.Matching);
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
    public async Task RebuildIndex_preserves_external_rows_and_trashed()
    {
        var index = new SqliteSkillIndex(_dbPath);
        var externalFolder = SkillFolder("ext-keep");
        Directory.CreateDirectory(externalFolder);
        await File.WriteAllTextAsync(Path.Combine(externalFolder, "SKILL.md"), "# foreign skill, no eling frontmatter\n");
        await index.AdoptExternalScanResultAsync(
            new Skill.Skill("ext-keep", "external desc", "external body", origin: SkillOrigin.External));

        var service = NewService(index);
        var created = (await service.SaveAsync(NewInput("my-skill"))).Skill;
        await service.DeleteAsync("my-skill");

        await service.RebuildIndexAsync();

        // Trashed skills survive a rebuild: their names stay occupied and the
        // trashed marker is preserved, per the trash-uniqueness rule. The
        // re-activated content is restored only via RestoreAsync.
        var everything = await service.ListAsync(includeTrashed: true, origin: null);
        Assert.Equal(2, everything.Items.Count);
        Assert.Contains(everything.Items, s => s.Name == "ext-keep");
        Assert.Contains(everything.Items, s => s.Name == "my-skill");

        // The trashed skill is still excluded from the active view.
        Assert.Null(await service.GetByNameAsync("my-skill"));

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

    [Fact]
    public async Task Adopt_external_skill_rewrites_file_and_flips_index()
    {
        var index = new SqliteSkillIndex(_dbPath);
        var document =
            "---\nname: ext-skill\ndescription: external desc\ntags:\n  - Alpha\n  - beta beta\nsource:\n  upstream: example.test/upstream\n  tracked: true\n---\n# ext-skill\n\nBody verbatim.\n";
        await SeedExternalAsync(index, "ext-skill", document);
        var externalRow = await index.GetByNameAsync("ext-skill");
        Assert.NotNull(externalRow);
        Assert.Equal(SkillOrigin.External, externalRow!.Origin);

        var service = NewService(index);
        var result = await service.AdoptAsync("ext-skill");

        Assert.Equal(externalRow.Id, result.Id);
        Assert.Equal("ext-skill", result.Name);
        Assert.Equal(SkillOrigin.Eling, result.Origin);
        Assert.True(result.WasExternal);
        Assert.Equal(SkillFile("ext-skill"), result.Path);

        var raw = await File.ReadAllTextAsync(SkillFile("ext-skill"));
        Assert.Contains("Body verbatim.", raw);
        Assert.Contains("createdAt:", raw);
        Assert.Contains("updatedAt:", raw);
        Assert.Contains(externalRow.Id.Value, raw);

        // The rewritten file round-trips through the strict parser (it is a full eling skill now).
        var stored = await new FileSystemSkillStorage(_root).GetByNameAsync("ext-skill");
        Assert.NotNull(stored);
        Assert.Equal(externalRow.Id, stored!.Id);
        Assert.Equal("ext-skill", stored.Name);
        Assert.Equal("external desc", stored.Description);
        Assert.Equal("# ext-skill\n\nBody verbatim.\n", stored.Body);
        Assert.Equal("example.test/upstream", stored.Source?.Upstream);
        Assert.True(stored.Source?.Tracked);
        Assert.Contains("alpha", stored.Tags);
        Assert.Contains("beta", stored.Tags);

        // The index row flipped origin, keeping its identity.
        var row = await index.GetByNameAsync("ext-skill");
        Assert.Equal(externalRow.Id, row!.Id);
        Assert.Equal(SkillOrigin.Eling, row.Origin);
        var externalOnly = await index.ListAsync(includeTrashed: false, origin: "external");
        Assert.Empty(externalOnly);
    }

    [Fact]
    public async Task Adopt_unparseable_frontmatter_refuses_and_leaves_everything_unchanged()
    {
        var index = new SqliteSkillIndex(_dbPath);
        var document = "---\ndescription: [unclosed\n---\n# body\n";
        await SeedExternalAsync(index, "broken-skill", document);
        var service = NewService(index);

        var ex = await Assert.ThrowsAsync<SkillValidationException>(() => service.AdoptAsync("broken-skill"));
        Assert.Contains("frontmatter", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Fix the frontmatter YAML first", ex.Message);

        // File byte-identical, row still external.
        Assert.Equal(document, await File.ReadAllTextAsync(SkillFile("broken-skill")));
        var row = await index.GetByNameAsync("broken-skill");
        Assert.Equal(SkillOrigin.External, row!.Origin);
    }

    [Fact]
    public async Task Adopt_missing_description_refuses_and_leaves_file_unchanged()
    {
        var index = new SqliteSkillIndex(_dbPath);
        var document = "---\nname: ext-desc\n---\n# body\n";
        await SeedExternalAsync(index, "ext-desc", document);
        var service = NewService(index);

        await Assert.ThrowsAsync<SkillValidationException>(() => service.AdoptAsync("ext-desc"));
        Assert.Equal(document, await File.ReadAllTextAsync(SkillFile("ext-desc")));
        Assert.Equal(SkillOrigin.External, (await index.GetByNameAsync("ext-desc"))!.Origin);
    }

    [Fact]
    public async Task Adopt_frontmatter_name_mismatch_refuses_and_leaves_file_unchanged()
    {
        var index = new SqliteSkillIndex(_dbPath);
        var document = "---\nname: other-name\ndescription: desc\n---\n# body\n";
        await SeedExternalAsync(index, "ext-mismatch", document);
        var service = NewService(index);

        await Assert.ThrowsAsync<SkillValidationException>(() => service.AdoptAsync("ext-mismatch"));
        Assert.Equal(document, await File.ReadAllTextAsync(SkillFile("ext-mismatch")));
        Assert.Equal(SkillOrigin.External, (await index.GetByNameAsync("ext-mismatch"))!.Origin);
    }

    [Fact]
    public async Task Adopt_eling_skill_throws_conflict()
    {
        var service = NewService();
        await service.SaveAsync(NewInput("my-skill"));

        var ex = await Assert.ThrowsAsync<SkillConflictException>(() => service.AdoptAsync("my-skill"));
        Assert.Contains("already Eling-managed", ex.Message);
    }

    [Fact]
    public async Task Adopt_unknown_name_throws_notfound()
    {
        var service = NewService();

        await Assert.ThrowsAsync<SkillNotFoundException>(() => service.AdoptAsync("nope-skill"));
    }

    [Fact]
    public async Task Adopt_trashed_skill_refuses_and_leaves_trash_untouched()
    {
        var index = new SqliteSkillIndex(_dbPath);
        var document = "---\ndescription: external desc\n---\n# body\n";
        await SeedExternalAsync(index, "ext-trashed", document);
        var service = NewService(index);
        await service.DeleteAsync("ext-trashed");

        var trashFile = Path.Combine(TrashFolder("ext-trashed"), "SKILL.md");
        var before = await File.ReadAllTextAsync(trashFile);

        await Assert.ThrowsAsync<SkillValidationException>(() => service.AdoptAsync("ext-trashed"));
        Assert.Equal(before, await File.ReadAllTextAsync(trashFile));
        Assert.Equal(SkillOrigin.External, (await index.GetByNameAsync("ext-trashed"))!.Origin);
    }

    [Fact]
    public async Task Adopt_without_index_row_inserts_new_eling_row()
    {
        // Legacy state: a folder that was never indexed (e.g. the catalog was
        // never scanned). Adoption succeeds and inserts the row as eling.
        var document = "---\ndescription: legacy desc\n---\n# legacy\n\nBody.\n";
        Directory.CreateDirectory(SkillFolder("legacy-skill"));
        await File.WriteAllTextAsync(SkillFile("legacy-skill"), document);

        var service = NewService();
        var result = await service.AdoptAsync("legacy-skill");

        Assert.Equal(SkillOrigin.Eling, result.Origin);
        var row = await service.GetByNameAsync("legacy-skill");
        Assert.Equal(SkillOrigin.Eling, row!.Origin);
        Assert.Contains("createdAt:", await File.ReadAllTextAsync(SkillFile("legacy-skill")));
    }

    [Fact]
    public async Task Adopt_preserves_body_byte_for_byte()
    {
        var index = new SqliteSkillIndex(_dbPath);
        var body = "# Title\n\nstep 1  \nstep 2\n\n```\ncode { }\n```\n";
        var document = "---\ndescription: desc\n---\n" + body;
        await SeedExternalAsync(index, "body-skill", document);

        var service = NewService(index);
        await service.AdoptAsync("body-skill");

        var raw = await File.ReadAllTextAsync(SkillFile("body-skill"));
        Assert.EndsWith(body, raw);
        var stored = await new FileSystemSkillStorage(_root).GetByNameAsync("body-skill");
        Assert.Equal(body, stored!.Body);
    }

    [Fact]
    public async Task Adopt_then_save_is_allowed()
    {
        var index = new SqliteSkillIndex(_dbPath);
        var document = "---\ndescription: external desc\n---\n# body\n";
        await SeedExternalAsync(index, "ext-adoptable", document);

        var service = NewService(index);
        await service.AdoptAsync("ext-adoptable");

        // After adoption the skill is a full eling skill: save is allowed.
        var updated = (await service.SaveAsync(NewInput("ext-adoptable", "now editable", "# rewritten\n"))).Skill;
        Assert.Equal(SkillOrigin.Eling, updated.Origin);
        var stored = await service.GetByNameAsync("ext-adoptable");
        Assert.Equal("now editable", stored!.Description);
    }

    private async Task SeedExternalAsync(ISkillIndex index, string name, string document)
    {
        Directory.CreateDirectory(SkillFolder(name));
        await File.WriteAllTextAsync(SkillFile(name), document);
        await index.AdoptExternalScanResultAsync(
            new Skill.Skill(name, "external desc", "external body", origin: SkillOrigin.External));
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
