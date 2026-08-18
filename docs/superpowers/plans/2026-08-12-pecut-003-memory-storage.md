# Canonical Markdown Memory Storage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement canonical Markdown persistence for Memory in `src/backend/Eling.Storage` and unit/integration tests in `tests/Eling.Storage.Tests`.

**Architecture:** Filesystem-based Markdown storage (`.eling/memories/<MemoryId>.md`) with YAML front-matter headers using `YamlDotNet`.

**Tech Stack:** .NET 10, C# 14, YamlDotNet, xUnit.

## Global Constraints
- Work ONLY in `src/backend/Eling.Storage/` and `tests/Eling.Storage.Tests/`.
- Do NOT touch `Eling.Index`, `Eling.Graph`, `Eling.Mcp`, `Eling.Server`, or `src/frontend/`.
- Do NOT maintain second JSON representation or SQLite DB/index.
- MemoryId is filename (`<MemoryId>.md`). Prevent path traversal attacks.

---

### Task 1: Add `YamlDotNet` to `Eling.Storage`

**Files:**
- Modify: `src/backend/Eling.Storage/Eling.Storage.csproj`

- [ ] **Step 1: Add `YamlDotNet` package reference**
Run: `dotnet add src/backend/Eling.Storage/Eling.Storage.csproj package YamlDotNet`

- [ ] **Step 2: Build `Eling.Storage`**
Run: `dotnet build src/backend/Eling.Storage/Eling.Storage.csproj`

---

### Task 2: Create `IMemoryStorage` Interface and Front-Matter DTOs

**Files:**
- Create: `src/backend/Eling.Storage/IMemoryStorage.cs`
- Create: `src/backend/Eling.Storage/MemoryFrontMatter.cs`

- [ ] **Step 1: Create `IMemoryStorage.cs`**

```csharp
using Eling.Core;

namespace Eling.Storage;

public interface IMemoryStorage
{
    Task SaveAsync(Memory memory);
    Task<Memory?> GetByIdAsync(MemoryId id);
    Task<bool> DeleteAsync(MemoryId id);
    Task<IReadOnlyCollection<Memory>> ListAllAsync();
}
```

- [ ] **Step 2: Create `MemoryFrontMatter.cs`**

```csharp
using YamlDotNet.Serialization;

namespace Eling.Storage;

internal class MemoryFrontMatter
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = string.Empty;

    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty;

    [YamlMember(Alias = "status")]
    public string Status { get; set; } = string.Empty;

    [YamlMember(Alias = "tags")]
    public List<string>? Tags { get; set; }

    [YamlMember(Alias = "created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [YamlMember(Alias = "updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [YamlMember(Alias = "source")]
    public string? Source { get; set; }
}
```

- [ ] **Step 3: Build `Eling.Storage`**
Run: `dotnet build src/backend/Eling.Storage/Eling.Storage.csproj`

---

### Task 3: Implement `FileSystemMemoryStorage`

**Files:**
- Create: `src/backend/Eling.Storage/FileSystemMemoryStorage.cs`
- Remove: `src/backend/Eling.Storage/Class1.cs`

- [ ] **Step 1: Write `FileSystemMemoryStorage.cs`**

```csharp
using Eling.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Eling.Storage;

public class FileSystemMemoryStorage : IMemoryStorage
{
    private readonly string _memoriesDir;
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public FileSystemMemoryStorage(string rootPath = ".eling")
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        _memoriesDir = Path.GetFullPath(Path.Combine(rootPath, "memories"));

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    private string GetFilePath(MemoryId id)
    {
        var filename = $"{id.Value}.md";
        var fullPath = Path.GetFullPath(Path.Combine(_memoriesDir, filename));

        if (!fullPath.StartsWith(_memoriesDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid memory ID or directory traversal attempt.", nameof(id));
        }

        return fullPath;
    }

    public async Task SaveAsync(Memory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        Directory.CreateDirectory(_memoriesDir);

        var filePath = GetFilePath(memory.Id);

        var frontMatter = new MemoryFrontMatter
        {
            Id = memory.Id.Value,
            Type = memory.Type.ToString().ToLowerInvariant(),
            Status = memory.Status.ToString().ToLowerInvariant(),
            Tags = memory.Tags.Count > 0 ? memory.Tags.ToList() : null,
            CreatedAt = memory.CreatedAt,
            UpdatedAt = memory.UpdatedAt,
            Source = memory.Source
        };

        var yaml = _serializer.Serialize(frontMatter).TrimEnd();
        var markdown = $"---\n{yaml}\n---\n{memory.Content}";

        await File.WriteAllTextAsync(filePath, markdown);
    }

    public async Task<Memory?> GetByIdAsync(MemoryId id)
    {
        var filePath = GetFilePath(id);

        if (!File.Exists(filePath))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(filePath);
        return ParseMemory(text);
    }

    public Task<bool> DeleteAsync(MemoryId id)
    {
        var filePath = GetFilePath(id);

        if (!File.Exists(filePath))
        {
            return Task.FromResult(false);
        }

        File.Delete(filePath);
        return Task.FromResult(true);
    }

    public async Task<IReadOnlyCollection<Memory>> ListAllAsync()
    {
        if (!Directory.Exists(_memoriesDir))
        {
            return Array.Empty<Memory>();
        }

        var files = Directory.GetFiles(_memoriesDir, "*.md");
        var memories = new List<Memory>();

        foreach (var file in files)
        {
            try
            {
                var text = await File.ReadAllTextAsync(file);
                var memory = ParseMemory(text);
                if (memory != null)
                {
                    memories.Add(memory);
                }
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException)
            {
                // Skip or handle corrupt files during bulk list if necessary
                throw;
            }
        }

        return memories.AsReadOnly();
    }

    private Memory ParseMemory(string rawMarkdown)
    {
        if (string.IsNullOrWhiteSpace(rawMarkdown))
        {
            throw new InvalidDataException("Memory Markdown file is empty.");
        }

        var parts = rawMarkdown.Split(new[] { "\n---\n", "\r\n---\r\n", "---\n", "---\r\n" }, StringSplitOptions.None);

        if (parts.Length < 3 || !string.IsNullOrWhiteSpace(parts[0]))
        {
            throw new InvalidDataException("Malformed Memory Markdown: Missing valid YAML front matter delimiters.");
        }

        var yamlBlock = parts[1];
        var content = string.Join("---\n", parts.Skip(2)).TrimStart('\r', '\n');

        MemoryFrontMatter frontMatter;
        try
        {
            frontMatter = _deserializer.Deserialize<MemoryFrontMatter>(yamlBlock);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Malformed YAML front matter.", ex);
        }

        if (frontMatter == null || string.IsNullOrWhiteSpace(frontMatter.Id))
        {
            throw new InvalidDataException("Malformed Memory: Missing Id in front matter.");
        }

        if (!Enum.TryParse<MemoryType>(frontMatter.Type, true, out var memoryType))
        {
            throw new InvalidDataException($"Unknown MemoryType: '{frontMatter.Type}'.");
        }

        if (!Enum.TryParse<MemoryStatus>(frontMatter.Status, true, out var memoryStatus))
        {
            throw new InvalidDataException($"Unknown MemoryStatus: '{frontMatter.Status}'.");
        }

        return new Memory(
            type: memoryType,
            content: content,
            tags: frontMatter.Tags,
            source: frontMatter.Source,
            status: memoryStatus,
            id: MemoryId.Parse(frontMatter.Id),
            createdAt: frontMatter.CreatedAt,
            updatedAt: frontMatter.UpdatedAt);
    }
}
```

- [ ] **Step 2: Remove scaffold `Class1.cs`**
Remove: `src/backend/Eling.Storage/Class1.cs`

- [ ] **Step 3: Build `Eling.Storage`**
Run: `dotnet build src/backend/Eling.Storage/Eling.Storage.csproj`

---

### Task 4: Integration Tests in `Eling.Storage.Tests`

**Files:**
- Create: `tests/Eling.Storage.Tests/FileSystemMemoryStorageTests.cs`
- Remove: `tests/Eling.Storage.Tests/UnitTest1.cs`

- [ ] **Step 1: Write `FileSystemMemoryStorageTests.cs`**

```csharp
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
    public async Task GetByIdAsync_ThrowsInvalidDataException_WhenMarkdownIsMalformed()
    {
        var memory = new Memory(MemoryType.Fact, "Some content");
        await _storage.SaveAsync(memory);

        var filePath = Path.Combine(_tempDir, "memories", $"{memory.Id.Value}.md");
        await File.WriteAllTextAsync(filePath, "invalid content without front matter");

        await Assert.ThrowsAsync<InvalidDataException>(() => _storage.GetByIdAsync(memory.Id));
    }
}
```

- [ ] **Step 2: Remove scaffold `UnitTest1.cs`**
Remove: `tests/Eling.Storage.Tests/UnitTest1.cs`

- [ ] **Step 3: Run solution build & tests**
Run: `dotnet build Eling.slnx && dotnet test Eling.slnx`
