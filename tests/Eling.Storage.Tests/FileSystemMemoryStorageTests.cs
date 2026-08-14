using System.Globalization;
using Eling.Core;
using Eling.Storage;

namespace Eling.Storage.Tests;

public class FileSystemMemoryStorageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemMemoryStorage _storage;

    public FileSystemMemoryStorageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling_tests_" + Guid.NewGuid().ToString("N"));
        _storage = new FileSystemMemoryStorage(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public async Task SaveAsync_CreatesMemoriesDirectoryAndFile()
    {
        var memory = new Memory(MemoryType.Fact, "User prefers dark mode", new[] { "ui" }, "prompt");

        await _storage.SaveAsync(memory);

        var expectedFile = Path.Combine(_tempDir, "memories", $"{memory.Id.Value}.md");
        Assert.True(File.Exists(expectedFile));

        var content = await File.ReadAllTextAsync(expectedFile);
        Assert.Contains("id: " + memory.Id.Value, content);
        Assert.Contains("type: fact", content);
        Assert.Contains("status: active", content);
        Assert.Contains("User prefers dark mode", content);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsSavedMemory()
    {
        var memory = new Memory(MemoryType.Preference, "Use tabs instead of spaces", new[] { "code" }, "settings");
        await _storage.SaveAsync(memory);

        var fetched = await _storage.GetByIdAsync(memory.Id);

        Assert.NotNull(fetched);
        Assert.Equal(memory.Id, fetched.Id);
        Assert.Equal(memory.Type, fetched.Type);
        Assert.Equal(memory.Status, fetched.Status);
        Assert.Equal(memory.Content, fetched.Content);
        Assert.Equal(memory.Tags, fetched.Tags);
        Assert.Equal(memory.Source, fetched.Source);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        var nonExistentId = MemoryId.NewId();
        var result = await _storage.GetByIdAsync(nonExistentId);
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_RemovesFileAndReturnsTrue()
    {
        var memory = new Memory(MemoryType.Note, "Temporary note");
        await _storage.SaveAsync(memory);

        var deleted = await _storage.DeleteAsync(memory.Id);
        Assert.True(deleted);

        var fetched = await _storage.GetByIdAsync(memory.Id);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task DeleteAsync_ReturnsFalse_WhenNotFound()
    {
        var deleted = await _storage.DeleteAsync(MemoryId.NewId());
        Assert.False(deleted);
    }

    [Fact]
    public async Task ListAllAsync_ReturnsMultipleMemories()
    {
        var mem1 = new Memory(MemoryType.Decision, "Architecture decision 1");
        var mem2 = new Memory(MemoryType.Lesson, "Lesson 1");

        await _storage.SaveAsync(mem1);
        await _storage.SaveAsync(mem2);

        var list = await _storage.ListAllAsync();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, m => m.Id == mem1.Id);
        Assert.Contains(list, m => m.Id == mem2.Id);
    }

    [Fact]
    public async Task SaveAsync_RoundTripsExactTimestamps()
    {
        var createdAt = DateTimeOffset.Parse(
            "2026-08-13T15:27:02.7938172+07:00",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        var updatedAt = createdAt.AddHours(2);
        var memory = new Memory(MemoryType.Fact, "timestamped content", createdAt: createdAt, updatedAt: updatedAt);

        await _storage.SaveAsync(memory);

        var fetched = await _storage.GetByIdAsync(memory.Id);
        Assert.NotNull(fetched);
        Assert.Equal(createdAt, fetched.CreatedAt);
        Assert.Equal(updatedAt, fetched.UpdatedAt);
    }

    [Fact]
    public async Task GetByIdAsync_ReadsLegacyDateTimeOffsetDump()
    {
        var memory = new Memory(MemoryType.Fact, "legacy file content");
        var dirPath = Path.Combine(_tempDir, "memories");
        Directory.CreateDirectory(dirPath);
        var filePath = Path.Combine(dirPath, $"{memory.Id.Value}.md");
        var legacyFrontMatter = $"""
            id: {memory.Id.Value}
            type: fact
            status: active
            tags:
            - database
            created_at: &o0
              utc_date_time: 2026-08-13T15:27:02.7938172Z
              ticks: 639222316227938172
              offset: 00:00:00
              day_of_week: Thursday
            updated_at: *o0
            source: system-spec
            """;
        await File.WriteAllTextAsync(filePath, $"---\n{legacyFrontMatter}\n---\n{memory.Content}");

        var fetched = await _storage.GetByIdAsync(memory.Id);

        Assert.NotNull(fetched);
        var expected = DateTimeOffset.Parse(
            "2026-08-13T15:27:02.7938172Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        Assert.Equal(expected, fetched.CreatedAt);
        Assert.Equal(expected, fetched.UpdatedAt);
    }

    [Fact]
    public async Task GetByIdAsync_ThrowsInvalidDataException_WhenMarkdownIsMalformed()
    {
        var memory = new Memory(MemoryType.Fact, "Some content");
        await _storage.SaveAsync(memory);

        var filePath = Path.Combine(_tempDir, "memories", $"{memory.Id.Value}.md");
        await File.WriteAllTextAsync(filePath, "invalid content without front matter");

        await Assert.ThrowsAsync<InvalidDataException>(() => _storage.GetByIdAsync(memory.Id));
    }

    [Fact]
    public async Task GetByIdAsync_PreservesFrontMatterDelimitersInsideContent()
    {
        var content = "Before\n---\na thematic break\n---\nAfter";
        var memory = new Memory(MemoryType.Note, content);
        await _storage.SaveAsync(memory);

        var fetched = await _storage.GetByIdAsync(memory.Id);

        Assert.NotNull(fetched);
        Assert.Equal(content, fetched.Content);
    }

    [Fact]
    public void MemoryId_RejectsTraversalValues()
    {
        var hostileValues = new[]
        {
            "../escape",
            @"..\escape",
            "memories/escape",
            @"memories\escape",
            "..%2f..%2fescape",
            "a/../escape"
        };

        foreach (var value in hostileValues)
        {
            Assert.Throws<ArgumentException>(() => new MemoryId(value));
        }
    }

    [Fact]
    public async Task Storage_DoesNotEscapeMemoriesDirectory()
    {
        var memory = new Memory(MemoryType.Fact, "confined content");
        await _storage.SaveAsync(memory);

        var memoriesDir = Path.Combine(_tempDir, "memories");
        var siblingDir = Path.Combine(_tempDir, "memories-evil");
        Directory.CreateDirectory(siblingDir);

        var originalFile = Path.Combine(memoriesDir, $"{memory.Id.Value}.md");
        var escapedFile = Path.Combine(siblingDir, $"{memory.Id.Value}.md");
        File.Move(originalFile, escapedFile);

        Assert.Null(await _storage.GetByIdAsync(memory.Id));

        var list = await _storage.ListAllAsync();
        Assert.DoesNotContain(list, m => m.Id == memory.Id);
    }
}
