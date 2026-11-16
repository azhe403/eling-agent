using Eling.Core.Skill;
using Eling.Core.Skill.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Eling.Core.Tests;

public sealed class SqliteSkillIndexTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "eling-skill-index-" + Guid.NewGuid().ToString("N") + ".db");

    private SqliteSkillIndex NewIndex() => new(_dbPath);

    private static Skill.Skill NewSkill(
        string name,
        string description = "does useful things",
        string body = "# body",
        SkillOrigin origin = SkillOrigin.Eling) =>
        new(name, description, body, null, null, null, null, null, origin);

    private static string NameOf(SkillSearchResult hit) =>
        Path.GetFileName(Path.GetDirectoryName(hit.Path)!)!;

    [Fact]
    public async Task Skill_SkillIndex_Index_then_Search_ranks_porter_match()
    {
        var index = NewIndex();
        await index.IndexAsync(NewSkill(
            "blazor-forms",
            "Build forms and validate data in Blazor.",
            "blazor form validation blazor input blazor editform"));
        await index.IndexAsync(NewSkill(
            "blazor-table",
            "Render rows in a grid.",
            "table render rows grid"));
        await index.IndexAsync(NewSkill(
            "git-hygiene",
            "Git hygiene checklist for commits.",
            "git status staging lease commit"));

        var hits = await index.SearchAsync("blazor");

        Assert.Equal(2, hits.Count);
        Assert.Equal("blazor-forms", NameOf(hits.First()));
        Assert.True(hits.First().Rank >= hits.Last().Rank);
        Assert.DoesNotContain(hits, h => NameOf(h) == "git-hygiene");
    }

    [Fact]
    public async Task Skill_SkillIndex_Index_twice_updates_not_duplicates()
    {
        var index = NewIndex();
        var skill = NewSkill("dup-check", "first description", "alphaunique content");
        await index.IndexAsync(skill);

        var updated = new Skill.Skill(
            "dup-check",
            "second description",
            "betaunique content",
            null, null, null, null,
            id: skill.Id,
            origin: SkillOrigin.Eling,
            createdAt: skill.CreatedAt,
            updatedAt: skill.UpdatedAt.AddMinutes(1));
        await index.IndexAsync(updated);

        Assert.Empty(await index.SearchAsync("alphaunique"));
        var hits = await index.SearchAsync("betaunique");
        Assert.Single(hits);
        Assert.Equal("dup-check", NameOf(hits.Single()));

        var listed = await index.ListAsync(includeTrashed: false, origin: null);
        Assert.Single(listed);
        Assert.Equal("second description", listed[0].Description);
    }

    [Fact]
    public async Task Skill_SkillIndex_Remove_drops_from_search()
    {
        var index = NewIndex();
        await index.IndexAsync(NewSkill("remove-me", "to be removed", "removableterm here"));

        await index.RemoveAsync("remove-me");

        Assert.Empty(await index.SearchAsync("removableterm"));
        Assert.Null(await index.GetByNameAsync("remove-me"));
        Assert.False(await index.IsNameTakenAsync("remove-me"));
    }

    [Fact]
    public async Task Skill_SkillIndex_List_excludes_trashed_by_default()
    {
        var index = NewIndex();
        await index.IndexAsync(NewSkill("active-one"));
        await index.IndexAsync(NewSkill("trashed-one"));
        await index.MarkTrashedAsync("trashed-one", trashed: true);

        var active = await index.ListAsync(includeTrashed: false, origin: null);
        var everything = await index.ListAsync(includeTrashed: true, origin: null);

        Assert.Single(active);
        Assert.Equal("active-one", active[0].Name);
        Assert.Equal(2, everything.Count);
    }

    [Fact]
    public async Task Skill_SkillIndex_Search_excludes_trashed_and_restores_on_untrash()
    {
        var index = NewIndex();
        await index.IndexAsync(NewSkill("trash-me", "searchable when active", "trashablecontent token"));
        await index.MarkTrashedAsync("trash-me", trashed: true);

        Assert.Empty(await index.SearchAsync("trashablecontent"));

        await index.MarkTrashedAsync("trash-me", trashed: false);

        var hits = await index.SearchAsync("trashablecontent");
        Assert.Single(hits);
    }

    [Fact]
    public async Task Skill_SkillIndex_Trashed_name_is_still_taken()
    {
        var index = NewIndex();
        await index.IndexAsync(NewSkill("held-name"));
        await index.MarkTrashedAsync("held-name", trashed: true);

        Assert.True(await index.IsNameTakenAsync("held-name"));
    }

    [Fact]
    public async Task Skill_SkillIndex_Rebuild_repopulates_from_files()
    {
        var index = NewIndex();
        await index.IndexAsync(NewSkill("stale-row", "stale", "stalecontent marker"));

        var fresh = NewSkill("rebuilt-row", "rebuilt description", "rebuiltcontent marker");
        await index.RebuildAsync([fresh]);

        Assert.Null(await index.GetByNameAsync("stale-row"));
        Assert.Empty(await index.SearchAsync("stalecontent"));
        Assert.Single(await index.SearchAsync("rebuiltcontent"));
        var rebuilt = await index.GetByNameAsync("rebuilt-row");
        Assert.NotNull(rebuilt);
        Assert.Equal("rebuilt description", rebuilt!.Description);
    }

    [Fact]
    public async Task Skill_SkillIndex_External_skill_roundtrips_as_external()
    {
        var index = NewIndex();
        var external = NewSkill(
            "foreign-tool-skill",
            "supplied by another tool",
            "externalbody content",
            SkillOrigin.External);

        await index.AdoptExternalScanResultAsync(external);

        var read = await index.GetByNameAsync("foreign-tool-skill");
        Assert.NotNull(read);
        Assert.Equal(SkillOrigin.External, read!.Origin);
        Assert.True(await index.IsNameTakenAsync("foreign-tool-skill"));

        // Re-adopting (rescan) must keep the row external and refresh content.
        var rescanned = new Skill.Skill(
            "foreign-tool-skill",
            "supplied by another tool, revised",
            "externalbodyrevised content",
            null, null, null, null,
            id: read.Id,
            origin: SkillOrigin.External,
            createdAt: read.CreatedAt,
            updatedAt: read.UpdatedAt.AddMinutes(1));
        await index.AdoptExternalScanResultAsync(rescanned);

        var reread = await index.GetByNameAsync("foreign-tool-skill");
        Assert.Equal(SkillOrigin.External, reread!.Origin);
        Assert.Equal("supplied by another tool, revised", reread.Description);
    }

    [Fact]
    public async Task Skill_SkillIndex_IndexAsync_rejects_external_origin()
    {
        var index = NewIndex();

        await Assert.ThrowsAsync<ArgumentException>(
            () => index.IndexAsync(NewSkill("external-via-index", origin: SkillOrigin.External)));
    }

    [Fact]
    public async Task Skill_SkillIndex_Search_filters_by_origin()
    {
        var index = NewIndex();
        await index.IndexAsync(NewSkill("eling-only", "managed by eling", "sharedterm eling"));
        await index.AdoptExternalScanResultAsync(
            NewSkill("external-only", "managed elsewhere", "sharedterm external", SkillOrigin.External));

        var externalHits = await index.SearchAsync("sharedterm", origin: "external");
        var elingHits = await index.SearchAsync("sharedterm", origin: "eling");

        Assert.Single(externalHits);
        Assert.Equal(SkillOrigin.External, externalHits.Single().Origin);
        Assert.Single(elingHits);
        Assert.Equal(SkillOrigin.Eling, elingHits.Single().Origin);
    }

    [Fact]
    public async Task Skill_SkillIndex_GetByName_roundtrips_tags_and_related_refs()
    {
        var index = NewIndex();
        var skill = new Skill.Skill(
            "full-roundtrip",
            "with refs",
            "body with refs",
            ["blazor", "forms"],
            [new RelatedReference("01m2s5a8sc8v6q2eeqneq3js6c", "global", "implements")],
            [new RelatedReference("01m3x9c1ypy6a0e1ht76rd8zv2", null, "example")],
            new SkillSource("https://example.test/upstream", Tracked: true));

        await index.IndexAsync(skill);

        var read = await index.GetByNameAsync("full-roundtrip");
        Assert.NotNull(read);
        Assert.Equal(new[] { "blazor", "forms" }, read!.Tags);
        var memoryRef = Assert.Single(read.RelatedMemories);
        Assert.Equal("01m2s5a8sc8v6q2eeqneq3js6c", memoryRef.Id);
        Assert.Equal("global", memoryRef.Scope);
        var snippetRef = Assert.Single(read.RelatedSnippets);
        Assert.Equal("01m3x9c1ypy6a0e1ht76rd8zv2", snippetRef.Id);
        Assert.Null(snippetRef.Scope);
        Assert.Equal("example", snippetRef.Relation);
    }

    [Fact]
    public async Task Skill_SkillIndex_Schema_idempotent()
    {
        var first = NewIndex();
        await first.IndexAsync(NewSkill("idempotent-one", "first open", "idempotent body"));

        // A second instance over the same file must not fail with "table exists".
        var second = NewIndex();
        var listed = await second.ListAsync(includeTrashed: false, origin: null);
        Assert.Single(listed);

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string)(await pragma.ExecuteScalarAsync())!);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            try
            {
                var path = _dbPath + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException ex)
            {
                // Best effort cleanup of a temp file; never fail a test over it.
                _ = ex.Message;
            }
        }
    }
}
