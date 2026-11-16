using Eling.Core.Skill;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Eling.Core.Tests;

public sealed class FileSystemSkillStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eling-skill-storage-" + Guid.NewGuid().ToString("N"));

    private string SkillsDir => Path.Combine(_root, "skills");
    private string TrashDir => Path.Combine(_root, ".trash", "skills");
    private string SkillFile(string name) => Path.Combine(SkillsDir, name, "SKILL.md");

    private static Skill.Skill NewSkill(string name, string body = "# body", string? description = null) =>
        new(name, description ?? "does things", body, null, null, null, null);

    [Fact]
    public async Task Save_then_GetByName_roundtrips_all_fields()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        var skill = new Skill.Skill(
            "my-skill",
            "does things",
            "# body\n\nsteps",
            ["blazor"],
            [new RelatedReference("01m2s5a8sc8v6q2eeqneq3js6c", "global", "implements")],
            [new RelatedReference("01m3x9c1ypy6a0e1ht76rd8zv2", null, "example")],
            new SkillSource("https://example.test/upstream", Tracked: true));

        await storage.SaveAsync(skill);

        var read = await storage.GetByNameAsync("my-skill");

        Assert.NotNull(read);
        Assert.Equal("my-skill", read!.Name);
        Assert.Equal("does things", read.Description);
        Assert.Equal(skill.Id, read.Id);
        Assert.Equal("# body\n\nsteps", read.Body.TrimEnd());
        Assert.Equal(new[] { "blazor" }, read.Tags);
        var memoryRef = Assert.Single(read.RelatedMemories);
        Assert.Equal("01m2s5a8sc8v6q2eeqneq3js6c", memoryRef.Id);
        Assert.Equal("global", memoryRef.Scope);
        Assert.Equal("implements", memoryRef.Relation);
        var snippetRef = Assert.Single(read.RelatedSnippets);
        Assert.Equal("01m3x9c1ypy6a0e1ht76rd8zv2", snippetRef.Id);
        Assert.Null(snippetRef.Scope);
        Assert.Equal("example", snippetRef.Relation);
        Assert.Equal("https://example.test/upstream", read.Source?.Upstream);
        Assert.True(read.Source?.Tracked);
        Assert.Equal(skill.CreatedAt, read.CreatedAt);
        Assert.Equal(skill.UpdatedAt, read.UpdatedAt);
        Assert.Equal(SkillOrigin.Eling, read.Origin);
    }

    [Fact]
    public async Task Save_writes_frontmatter_without_origin_and_preserves_ulid_across_overwrite()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        var skill = NewSkill("my-skill", "body one");
        await storage.SaveAsync(skill);

        var text = await File.ReadAllTextAsync(SkillFile("my-skill"));
        Assert.StartsWith("---\n", text);
        Assert.Contains($"id: {skill.Id.Value}", text);
        Assert.Contains("name: my-skill", text);
        Assert.Contains("\n---\n", text);
        Assert.DoesNotContain("origin:", text);

        skill.Description = "rewritten";
        await storage.SaveAsync(skill);

        var read = await storage.GetByNameAsync("my-skill");
        Assert.NotNull(read);
        Assert.Equal(skill.Id, read!.Id);
        Assert.Equal("rewritten", read.Description);
        Assert.Equal("body one", read.Body);
    }

    [Fact]
    public async Task Save_blocked_by_trashed_name()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        var skill = NewSkill("my-skill");
        await storage.SaveAsync(skill);
        await storage.MoveToTrashAsync("my-skill");

        var replacement = NewSkill("my-skill");
        await Assert.ThrowsAsync<Skill.Storage.SkillConflictException>(() => storage.SaveAsync(replacement));
        Assert.False(Directory.Exists(Path.Combine(SkillsDir, "my-skill")));
    }

    [Fact]
    public async Task Save_with_invalid_sibling_path_leaves_no_partial_skill()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        var skill = NewSkill("my-skill");

        await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync(skill,
            [new Skill.Storage.SkillSiblingFile("../escape.txt", [1, 2, 3])]));

        Assert.False(Directory.Exists(Path.Combine(SkillsDir, "my-skill")));
        Assert.Empty(Directory.Exists(SkillsDir) ? Directory.GetDirectories(SkillsDir) : []);
    }

    [Fact]
    public async Task Save_with_scope_less_memory_reference_fails_before_any_io()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        var skill = new Skill.Skill(
            "my-skill",
            "does things",
            "body",
            null,
            [new RelatedReference("01m2s5a8sc8v6q2eeqneq3js6c", null, "implements")],
            null,
            null,
            null);

        await Assert.ThrowsAsync<Skill.Storage.SkillFrontMatterException>(() => storage.SaveAsync(skill));
        Assert.False(Directory.Exists(SkillsDir));
    }

    [Fact]
    public async Task Save_refuses_externally_origined_skill()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        var skill = NewSkill("my-skill");
        skill.Origin = SkillOrigin.External;

        await Assert.ThrowsAsync<Skill.Storage.SkillStorageException>(() => storage.SaveAsync(skill));
        Assert.False(Directory.Exists(Path.Combine(SkillsDir, "my-skill")));
    }

    [Fact]
    public async Task Save_writes_sibling_files_verbatim()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        var skill = NewSkill("my-skill");
        var scriptBytes = new byte[] { 0x23, 0x21, 0x00, 0xFF, 0x7F };

        await storage.SaveAsync(skill,
        [
            new Skill.Storage.SkillSiblingFile("scripts/run.ps1", scriptBytes),
            new Skill.Storage.SkillSiblingFile("notes.txt", "hello"u8.ToArray()),
        ]);

        Assert.Equal(scriptBytes, await File.ReadAllBytesAsync(Path.Combine(SkillsDir, "my-skill", "scripts", "run.ps1")));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(SkillsDir, "my-skill", "notes.txt")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("C:absolute.txt")]
    [InlineData("\\rooted.txt")]
    [InlineData("sub\\..\\escape.txt")]
    [InlineData("a//b.txt")]
    [InlineData("a/./b.txt")]
    public async Task Save_rejects_sibling_paths_that_escape_the_folder(string relativePath)
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        var skill = NewSkill("my-skill");

        await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync(skill,
            [new Skill.Storage.SkillSiblingFile(relativePath, [1])]));

        Assert.False(Directory.Exists(Path.Combine(SkillsDir, "my-skill")));
    }

    [Fact]
    public async Task Rename_moves_folder_and_keeps_ulid()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        var skill = NewSkill("old-name", "body keeps");
        await storage.SaveAsync(skill);

        await storage.RenameAsync("old-name", "new-name");

        Assert.False(Directory.Exists(Path.Combine(SkillsDir, "old-name")));
        Assert.True(Directory.Exists(Path.Combine(SkillsDir, "new-name")));

        var read = await storage.GetByNameAsync("new-name");
        Assert.NotNull(read);
        Assert.Equal(skill.Id, read!.Id);
        Assert.Equal("new-name", read.Name);
        Assert.Equal("body keeps", read.Body);
        Assert.Equal(skill.CreatedAt, read.CreatedAt);

        var text = await File.ReadAllTextAsync(SkillFile("new-name"));
        Assert.Contains("name: new-name", text);
        Assert.DoesNotContain("name: old-name", text);
    }

    [Fact]
    public async Task Rename_preserves_sibling_files()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        var skill = NewSkill("old-name");
        await storage.SaveAsync(skill, [new Skill.Storage.SkillSiblingFile("scripts/run.ps1", [9, 8, 7])]);

        await storage.RenameAsync("old-name", "new-name");

        Assert.Equal(
            new byte[] { 9, 8, 7 },
            await File.ReadAllBytesAsync(Path.Combine(SkillsDir, "new-name", "scripts", "run.ps1")));
    }

    [Fact]
    public async Task Rename_to_existing_name_throws_conflict_and_keeps_destination()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await storage.SaveAsync(NewSkill("old-name", "old body"));
        await storage.SaveAsync(NewSkill("new-name", "new body"));

        await Assert.ThrowsAsync<Skill.Storage.SkillConflictException>(() => storage.RenameAsync("old-name", "new-name"));

        Assert.True(Directory.Exists(Path.Combine(SkillsDir, "old-name")));
        var read = await storage.GetByNameAsync("new-name");
        Assert.Equal("new body", read!.Body);
    }

    [Fact]
    public async Task Rename_missing_skill_throws()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await Assert.ThrowsAsync<Skill.Storage.SkillStorageException>(() => storage.RenameAsync("absent-name", "other-name"));
    }

    [Fact]
    public async Task MoveToTrash_then_Restore_restores_folder()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        var skill = NewSkill("my-skill");
        await storage.SaveAsync(skill);

        await storage.MoveToTrashAsync("my-skill");

        Assert.False(Directory.Exists(Path.Combine(SkillsDir, "my-skill")));
        Assert.True(Directory.Exists(Path.Combine(TrashDir, "my-skill")));
        Assert.Null(await storage.GetByNameAsync("my-skill"));

        await storage.RestoreAsync("my-skill");

        Assert.True(Directory.Exists(Path.Combine(SkillsDir, "my-skill")));
        Assert.False(Directory.Exists(Path.Combine(TrashDir, "my-skill")));
        var read = await storage.GetByNameAsync("my-skill");
        Assert.Equal(skill.Id, read!.Id);
    }

    [Fact]
    public async Task MoveToTrash_when_trash_destination_exists_throws_conflict()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await storage.SaveAsync(NewSkill("my-skill"));
        await storage.MoveToTrashAsync("my-skill");

        // A "different, earlier-trashed" skill occupies the destination; simulate a stray active folder with the same name.
        Directory.CreateDirectory(Path.Combine(SkillsDir, "my-skill"));
        await File.WriteAllTextAsync(SkillFile("my-skill"), "stray");

        await Assert.ThrowsAsync<Skill.Storage.SkillConflictException>(() => storage.MoveToTrashAsync("my-skill"));
        Assert.True(Directory.Exists(Path.Combine(TrashDir, "my-skill")));
    }

    [Fact]
    public async Task MoveToTrash_missing_skill_throws()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await Assert.ThrowsAsync<Skill.Storage.SkillStorageException>(() => storage.MoveToTrashAsync("absent-name"));
    }

    [Fact]
    public async Task Restore_when_active_destination_exists_throws_conflict_and_keeps_trash()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await storage.SaveAsync(NewSkill("my-skill"));
        await storage.MoveToTrashAsync("my-skill");

        // Simulate an active folder recreated out-of-band while the trash row exists.
        Directory.CreateDirectory(Path.Combine(SkillsDir, "my-skill"));

        await Assert.ThrowsAsync<Skill.Storage.SkillConflictException>(() => storage.RestoreAsync("my-skill"));
        Assert.True(Directory.Exists(Path.Combine(TrashDir, "my-skill")));
    }

    [Fact]
    public async Task Restore_missing_skill_throws()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await Assert.ThrowsAsync<Skill.Storage.SkillStorageException>(() => storage.RestoreAsync("absent-name"));
    }

    [Fact]
    public async Task DeleteAsync_removes_trashed_skill_permanently_but_never_active_skill()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await storage.SaveAsync(NewSkill("active-skill"));
        await storage.SaveAsync(NewSkill("trashed-skill"));
        await storage.MoveToTrashAsync("trashed-skill");

        await Assert.ThrowsAsync<Skill.Storage.SkillStorageException>(() => storage.DeleteAsync("active-skill"));
        Assert.True(Directory.Exists(Path.Combine(SkillsDir, "active-skill")));

        await storage.DeleteAsync("trashed-skill");
        Assert.False(Directory.Exists(Path.Combine(TrashDir, "trashed-skill")));

        await Assert.ThrowsAsync<Skill.Storage.SkillStorageException>(() => storage.DeleteAsync("trashed-skill"));
    }

    [Fact]
    public async Task GetByName_returns_null_for_missing_folder()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        Assert.Null(await storage.GetByNameAsync("absent-name"));
    }

    [Fact]
    public async Task ListAll_skips_folders_without_SKILL_md()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await storage.SaveAsync(NewSkill("listed-skill"));

        var stray = Path.Combine(SkillsDir, "stray-folder");
        Directory.CreateDirectory(stray);
        await File.WriteAllTextAsync(Path.Combine(stray, "notes.txt"), "not a skill");

        var list = await storage.ListAllAsync();
        Assert.Equal(new[] { "listed-skill" }, list.Select(s => s.Name));
    }

    [Fact]
    public async Task ListAll_skips_invalid_folder_names_and_returns_ordinal_order()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await storage.SaveAsync(NewSkill("b-skill"));
        await storage.SaveAsync(NewSkill("a-skill"));
        Directory.CreateDirectory(Path.Combine(SkillsDir, "Not A Skill"));

        var list = await storage.ListAllAsync();
        Assert.Equal(new[] { "a-skill", "b-skill" }, list.Select(s => s.Name));
    }

    [Fact]
    public async Task ListAll_skips_malformed_frontmatter_with_warning()
    {
        var logger = new CapturingLogger<Skill.Storage.FileSystemSkillStorage>();
        var storage = new Skill.Storage.FileSystemSkillStorage(_root, logger);
        await storage.SaveAsync(NewSkill("broken-skill"));
        await File.WriteAllTextAsync(SkillFile("broken-skill"), "---\n{{{\n---\nbody");

        var list = await storage.ListAllAsync();

        Assert.Empty(list);
        Assert.Contains(logger.Messages, m => m.Contains("Skipping skill folder"));
    }

    [Fact]
    public async Task GetByName_throws_on_corrupt_frontmatter_and_leaves_no_partial_file()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await storage.SaveAsync(NewSkill("my-skill"));
        var file = SkillFile("my-skill");
        var original = await File.ReadAllTextAsync(file);

        await File.WriteAllTextAsync(file, "---\nthis is not: [valid: yaml\n---\nbody");

        await Assert.ThrowsAsync<Skill.Storage.SkillFrontMatterException>(() => storage.GetByNameAsync("my-skill"));
        // The failed read must not rewrite or truncate the file.
        Assert.Equal("---\nthis is not: [valid: yaml\n---\nbody", await File.ReadAllTextAsync(file));
        Assert.NotEqual(original, await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task GetByName_throws_on_frontmatter_name_folder_mismatch()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await storage.SaveAsync(NewSkill("my-skill"));
        var file = SkillFile("my-skill");
        var text = await File.ReadAllTextAsync(file);
        await File.WriteAllTextAsync(file, text.Replace("name: my-skill", "name: other-skill"));

        await Assert.ThrowsAsync<Skill.Storage.SkillFrontMatterException>(() => storage.GetByNameAsync("my-skill"));
    }

    [Fact]
    public async Task GetByName_rejects_invalid_relation_vocabulary()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await storage.SaveAsync(NewSkill("my-skill"));
        var file = SkillFile("my-skill");
        var text = await File.ReadAllTextAsync(file);
        var doctored = text.Replace(
            "createdAt:",
            """
            relatedMemories:
            - id: "01m2s5a8sc8v6q2eeqneq3js6c"
              scope: "global"
              relation: "bogus-relation"
            createdAt:
            """);
        // Ensure the replacement actually produced the injected reference.
        Assert.NotEqual(text, doctored);
        await File.WriteAllTextAsync(file, doctored);

        var ex = await Assert.ThrowsAsync<Skill.Storage.SkillFrontMatterException>(() => storage.GetByNameAsync("my-skill"));
        Assert.Contains("bogus-relation", ex.Message);
    }

    [Fact]
    public async Task GetByName_rejects_memory_reference_with_invalid_scope()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await storage.SaveAsync(NewSkill("my-skill"));
        var file = SkillFile("my-skill");
        var text = await File.ReadAllTextAsync(file);
        var doctored = text.Replace(
            "createdAt:",
            """
            relatedMemories:
            - id: "01m2s5a8sc8v6q2eeqneq3js6c"
              scope: "moon"
              relation: "implements"
            createdAt:
            """);
        Assert.NotEqual(text, doctored);
        await File.WriteAllTextAsync(file, doctored);

        var ex = await Assert.ThrowsAsync<Skill.Storage.SkillFrontMatterException>(() => storage.GetByNameAsync("my-skill"));
        Assert.Contains("moon", ex.Message);
    }

    [Fact]
    public async Task GetByName_rejects_snippet_reference_that_carries_a_scope()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await storage.SaveAsync(NewSkill("my-skill"));
        var file = SkillFile("my-skill");
        var text = await File.ReadAllTextAsync(file);
        var doctored = text.Replace(
            "createdAt:",
            """
            relatedSnippets:
            - id: "01m3x9c1ypy6a0e1ht76rd8zv2"
              scope: "global"
              relation: "example"
            createdAt:
            """);
        Assert.NotEqual(text, doctored);
        await File.WriteAllTextAsync(file, doctored);

        await Assert.ThrowsAsync<Skill.Storage.SkillFrontMatterException>(() => storage.GetByNameAsync("my-skill"));
    }

    [Fact]
    public async Task ExistsAsync_requires_SKILL_md_not_just_a_folder()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        Assert.False(await storage.ExistsAsync("my-skill"));

        await storage.SaveAsync(NewSkill("my-skill"));
        Assert.True(await storage.ExistsAsync("my-skill"));

        Directory.Delete(Path.Combine(SkillsDir, "my-skill"), recursive: true);
        Directory.CreateDirectory(Path.Combine(SkillsDir, "my-skill"));
        Assert.False(await storage.ExistsAsync("my-skill"));
    }

    [Fact]
    public async Task Save_twice_overwriting_preserves_ulid_and_drops_no_folders()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        var skill = NewSkill("my-skill", "first");
        await storage.SaveAsync(skill);
        skill.Body = "second";
        await storage.SaveAsync(skill);

        Assert.Equal(new[] { "my-skill" }, Directory.GetDirectories(SkillsDir).Select(Path.GetFileName));
        var read = await storage.GetByNameAsync("my-skill");
        Assert.Equal(skill.Id, read!.Id);
        Assert.Equal("second", read.Body);
    }

    [Fact]
    public async Task Invalid_names_are_rejected_before_any_io()
    {
        var storage = new Skill.Storage.FileSystemSkillStorage(_root);
        await Assert.ThrowsAsync<ArgumentException>(() => storage.GetByNameAsync("../escape"));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ExistsAsync("Bad Name"));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.MoveToTrashAsync("bad/name"));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.RestoreAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync("trailing-"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
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
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<string> Messages = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
