using Eling.Core.Skill;
using Eling.Core.Skill.Scanning;
using Eling.Core.Skill.Storage;

using Xunit;

namespace Eling.Core.Tests;

public sealed class SkillCatalogScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eling-skill-scanner-" + Guid.NewGuid().ToString("N"));

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "eling-skill-scanner-" + Guid.NewGuid().ToString("N") + ".db");

    private string SkillsDir => Path.Combine(_root, "skills");
    private string SkillFolder(string name) => Path.Combine(SkillsDir, name);
    private string SkillFile(string name) => Path.Combine(SkillFolder(name), "SKILL.md");

    private SqliteSkillIndex NewIndex() => new(_dbPath);

    private SkillCatalogScanner NewScanner(ISkillIndex? index = null) =>
        new(index ?? NewIndex(), _root);

    private SkillService NewService(ISkillIndex? index = null) =>
        new(new FileSystemSkillStorage(_root), index ?? NewIndex());

    private static SkillSaveInput NewInput(string name, string description = "does useful things", string body = "# body\n\nsteps") =>
        new(name, description, body);

    private static string ElingDoc(string id, string name, string description = "does useful things", string body = "# body\n\nsteps") =>
        $"---\nid: {id}\nname: {name}\ndescription: {description}\ncreatedAt: 2026-11-16T00:00:00.0000000+00:00\nupdatedAt: 2026-11-16T00:00:00.0000000+00:00\n---\n{body}\n";

    private async Task WriteElingSkill(string name)
    {
        var id = System.Ulid.NewUlid().ToString().ToLowerInvariant();
        Directory.CreateDirectory(SkillFolder(name));
        await File.WriteAllTextAsync(SkillFile(name), ElingDoc(id, name));
    }

    private static string ExternalDoc(string name, string description = "supplied by another tool") =>
        $"---\nname: {name}\ndescription: {description}\n---\n# {name}\n\nexternally supplied instructions\n";

    private byte[][] SnapshotCatalogBytes() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(File.ReadAllBytes)
            .ToArray();

    [Fact]
    public async Task Scan_indexes_external_without_writing_files()
    {
        Directory.CreateDirectory(SkillFolder("external-helper"));
        await File.WriteAllTextAsync(SkillFile("external-helper"), ExternalDoc("external-helper"));
        await File.WriteAllTextAsync(Path.Combine(SkillFolder("external-helper"), "notes.txt"), "opaque payload\n");

        var before = SnapshotCatalogBytes();

        var index = NewIndex();
        var report = await NewScanner(index).ScanAsync();

        Assert.Equal(1, report.External);
        Assert.Equal(0, report.Discovered);
        Assert.Equal(0, report.Failed);

        var row = await index.GetByNameAsync("external-helper");
        Assert.NotNull(row);
        Assert.Equal(SkillOrigin.External, row!.Origin);

        var after = SnapshotCatalogBytes();
        Assert.Equal(before.Length, after.Length);
        for (var i = 0; i < before.Length; i++)
        {
            Assert.Equal(before[i], after[i]);
        }
    }

    [Fact]
    public async Task Scan_marks_missing_folders()
    {
        var index = NewIndex();
        var service = NewService(index);
        var saved = await service.SaveAsync(NewInput("doomed-skill"));

        Directory.Delete(SkillFolder("doomed-skill"), recursive: true);

        var report = await NewScanner(index).ScanAsync();

        Assert.Equal(1, report.Missing);
        Assert.Null(await index.GetByNameAsync("doomed-skill"));
        var (items, _, _) = await NewService(index).ListAsync(includeTrashed: false, origin: null);
        Assert.DoesNotContain(items, s => s.Name == "doomed-skill");
        Assert.NotEqual(default, saved.Id); // folder-based setup sanity check
    }

    [Fact]
    public async Task Scan_is_idempotent()
    {
        var index = NewIndex();
        await WriteElingSkill("eling-skill");
        Directory.CreateDirectory(SkillFolder("external-helper"));
        await File.WriteAllTextAsync(SkillFile("external-helper"), ExternalDoc("external-helper"));

        var first = await NewScanner(index).ScanAsync();
        Assert.Equal(1, first.Discovered);
        Assert.Equal(1, first.External);

        var second = await NewScanner(index).ScanAsync();
        Assert.Equal(0, second.Discovered);
        Assert.Equal(0, second.External);
        Assert.Equal(0, second.Missing);
        Assert.Equal(0, second.Failed);
        Assert.Equal(0, second.Renamed);
    }

    [Fact]
    public async Task Scan_skips_malformed_frontmatter()
    {
        Directory.CreateDirectory(SkillFolder("broken-skill"));
        await File.WriteAllTextAsync(
            SkillFile("broken-skill"),
            "---\ndescription: \"unterminated\n---\nbody text\n");

        var index = NewIndex();
        await WriteElingSkill("good-eling");
        Directory.CreateDirectory(SkillFolder("good-external"));
        await File.WriteAllTextAsync(SkillFile("good-external"), ExternalDoc("good-external"));

        var report = await NewScanner(index).ScanAsync();

        Assert.Equal(1, report.Failed);
        Assert.Equal(1, report.External);
        Assert.Equal(1, report.Discovered);
        Assert.NotNull(await index.GetByNameAsync("good-eling"));
        Assert.NotNull(await index.GetByNameAsync("good-external"));
    }

    [Fact]
    public async Task Scan_assigns_stable_ulid_to_external_across_rescans()
    {
        Directory.CreateDirectory(SkillFolder("external-helper"));
        await File.WriteAllTextAsync(SkillFile("external-helper"), ExternalDoc("external-helper"));

        var index = NewIndex();
        var first = await NewScanner(index).ScanAsync();
        var firstRow = await index.GetByNameAsync("external-helper");
        Assert.Equal(1, first.External);
        Assert.NotNull(firstRow);

        var second = await NewScanner(index).ScanAsync();
        var secondRow = await index.GetByNameAsync("external-helper");
        Assert.Equal(0, second.External);
        Assert.NotNull(secondRow);
        Assert.Equal(firstRow!.Id, secondRow!.Id);

        // The file is never written back: it still carries no Eling id.
        var content = await File.ReadAllTextAsync(SkillFile("external-helper"));
        Assert.DoesNotContain("id:", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_detects_folder_rename_by_ulid()
    {
        var index = NewIndex();
        var saved = await NewService(index).SaveAsync(NewInput("old-name"));

        // Simulate the index missing a rename: move the folder and rewrite the
        // name in frontmatter via storage (which does not touch the index).
        await new FileSystemSkillStorage(_root).RenameAsync("old-name", "new-name");

        var report = await NewScanner(index).ScanAsync();

        Assert.Equal(1, report.Renamed);
        Assert.Equal(0, report.Discovered);
        Assert.Equal(0, report.Missing);

        var renamed = await index.GetByNameAsync("new-name");
        Assert.NotNull(renamed);
        Assert.Equal(saved.Id, renamed!.Id);
        Assert.Null(await index.GetByNameAsync("old-name"));

        var (items, _, _) = await NewService(index).ListAsync(includeTrashed: false, origin: null);
        Assert.Single(items);
    }

    [Fact]
    public async Task Scan_keeps_trashed_rows_untouched()
    {
        var index = NewIndex();
        var service = NewService(index);
        await service.SaveAsync(NewInput("doomed-skill"));
        await service.DeleteAsync("doomed-skill");

        var report = await NewScanner(index).ScanAsync();

        Assert.Equal(0, report.Missing);
        var (items, total, _) = await service.ListAsync(includeTrashed: true, origin: null);
        Assert.Contains(items, s => s.Name == "doomed-skill");
        Assert.Equal(1, total);
    }

    public void Dispose()
    {
        TryDeleteDirectory(_root);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(_dbPath + suffix))
                {
                    File.Delete(_dbPath + suffix);
                }
            }
            catch (Exception)
            {
                // best effort temp cleanup
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // best effort temp cleanup
        }
    }
}
