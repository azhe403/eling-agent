# Memory Domain Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the initial `Memory` domain model, `MemoryId` identifier wrapper, `MemoryType` enum, and `MemoryStatus` enum in `src/backend/Eling.Core`, with comprehensive unit tests in `tests/Eling.Core.Tests`.

**Architecture:** Framework-independent domain entities inside `Eling.Core` using a `MemoryId` value object wrapper around `Ulid`.

**Tech Stack:** .NET 10, C# 14, Ulid NuGet package, xUnit.

## Global Constraints

- No EF Core, SQLite, ASP.NET, Markdown/JSON serialization, embeddings, vector search, scoring, or graph dependencies in `Eling.Core`.
- Wrap ULID in a `MemoryId` domain value object to prevent direct coupling across domain types.
- Framework-independent domain model.

---

### Task 1: Add `Ulid` Package to `Eling.Core` (COMPLETED)

**Files:**
- Modify: `src/backend/Eling.Core/Eling.Core.csproj`

- [x] **Step 1: Add `Ulid` package reference**
- [x] **Step 2: Build to verify restoration**
- [x] **Step 3: Commit**

---

### Task 2: Create `MemoryId` Identifier Wrapper

**Files:**
- Create: `src/backend/Eling.Core/MemoryId.cs`

- [ ] **Step 1: Create `MemoryId.cs` value object**

```csharp
using NUlid;

namespace Eling.Core;

public readonly record struct MemoryId
{
    public string Value { get; }

    public MemoryId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Ulid.TryParse(value, out _))
        {
            throw new ArgumentException("Invalid ULID format.", nameof(value));
        }
        Value = value;
    }

    public static MemoryId NewId() => new(Ulid.NewUlid().ToString());

    public static MemoryId Parse(string value) => new(value);

    public override string ToString() => Value;
}
```

- [ ] **Step 2: Build `Eling.Core`**

Run: `dotnet build src/backend/Eling.Core/Eling.Core.csproj`

- [ ] **Step 3: Commit**

```bash
git add src/backend/Eling.Core/MemoryId.cs
git commit -m "feat(core): add MemoryId value object wrapper around ULID"
```

---

### Task 3: Create `MemoryType` and `MemoryStatus` Enums

**Files:**
- Create: `src/backend/Eling.Core/MemoryType.cs`
- Create: `src/backend/Eling.Core/MemoryStatus.cs`

- [ ] **Step 1: Create `MemoryType.cs`**

```csharp
namespace Eling.Core;

public enum MemoryType
{
    Fact,
    Preference,
    Decision,
    Lesson,
    Note
}
```

- [ ] **Step 2: Create `MemoryStatus.cs`**

```csharp
namespace Eling.Core;

public enum MemoryStatus
{
    Active,
    Superseded,
    Archived
}
```

- [ ] **Step 3: Build to verify enums compile**

Run: `dotnet build src/backend/Eling.Core/Eling.Core.csproj`

- [ ] **Step 4: Commit**

```bash
git add src/backend/Eling.Core/MemoryType.cs src/backend/Eling.Core/MemoryStatus.cs
git commit -m "feat(core): add MemoryType and MemoryStatus enums"
```

---

### Task 4: Create `Memory` Domain Class

**Files:**
- Create: `src/backend/Eling.Core/Memory.cs`
- Remove: `src/backend/Eling.Core/Class1.cs`

- [ ] **Step 1: Write `Memory.cs` implementation**

```csharp
namespace Eling.Core;

public class Memory
{
    public MemoryId Id { get; }
    public MemoryType Type { get; }
    public MemoryStatus Status { get; set; }
    public string Content { get; set; }
    public IReadOnlyCollection<string> Tags { get; set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Source { get; set; }

    public Memory(
        MemoryType type,
        string content,
        IEnumerable<string>? tags = null,
        string? source = null,
        MemoryStatus status = MemoryStatus.Active,
        MemoryId? id = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        Id = id ?? MemoryId.NewId();
        Type = type;
        Status = status;
        Content = content;
        Tags = tags?.ToList().AsReadOnly() ?? (IReadOnlyCollection<string>)Array.Empty<string>();
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        UpdatedAt = updatedAt ?? CreatedAt;
        Source = source;
    }
}
```

- [ ] **Step 2: Remove scaffold `Class1.cs`**

- [ ] **Step 3: Build `Eling.Core`**

Run: `dotnet build src/backend/Eling.Core/Eling.Core.csproj`

- [ ] **Step 4: Commit**

```bash
git add src/backend/Eling.Core/Memory.cs
git rm src/backend/Eling.Core/Class1.cs
git commit -m "feat(core): implement Memory domain model"
```

---

### Task 5: Unit Tests in `Eling.Core.Tests`

**Files:**
- Create: `tests/Eling.Core.Tests/MemoryTests.cs`
- Remove: `tests/Eling.Core.Tests/UnitTest1.cs`

- [ ] **Step 1: Write `MemoryTests.cs`**

```csharp
using Eling.Core;

namespace Eling.Core.Tests;

public class MemoryTests
{
    [Fact]
    public void Constructor_InitializesDefaultValues()
    {
        var before = DateTimeOffset.UtcNow;
        var memory = new Memory(MemoryType.Fact, "User prefers dark mode");
        var after = DateTimeOffset.UtcNow;

        Assert.False(string.IsNullOrWhiteSpace(memory.Id.Value));
        Assert.Equal(MemoryType.Fact, memory.Type);
        Assert.Equal(MemoryStatus.Active, memory.Status);
        Assert.Equal("User prefers dark mode", memory.Content);
        Assert.Empty(memory.Tags);
        Assert.Null(memory.Source);
        Assert.InRange(memory.CreatedAt, before, after);
        Assert.Equal(memory.CreatedAt, memory.UpdatedAt);
    }

    [Fact]
    public void Constructor_SetsProvidedValues()
    {
        var id = MemoryId.NewId();
        var createdAt = DateTimeOffset.UtcNow.AddHours(-1);
        var updatedAt = DateTimeOffset.UtcNow;
        var tags = new[] { "pref", "ui" };

        var memory = new Memory(
            type: MemoryType.Preference,
            content: "Use dark theme",
            tags: tags,
            source: "user-prompt",
            status: MemoryStatus.Superseded,
            id: id,
            createdAt: createdAt,
            updatedAt: updatedAt);

        Assert.Equal(id, memory.Id);
        Assert.Equal(MemoryType.Preference, memory.Type);
        Assert.Equal(MemoryStatus.Superseded, memory.Status);
        Assert.Equal("Use dark theme", memory.Content);
        Assert.Equal(tags, memory.Tags);
        Assert.Equal("user-prompt", memory.Source);
        Assert.Equal(createdAt, memory.CreatedAt);
        Assert.Equal(updatedAt, memory.UpdatedAt);
    }

    [Fact]
    public void MemoryId_ValidatesUlidFormat()
    {
        Assert.Throws<ArgumentException>(() => new MemoryId("invalid-ulid"));
        var validId = MemoryId.NewId();
        Assert.Equal(validId.Value, MemoryId.Parse(validId.Value).Value);
    }

    [Fact]
    public void Constructor_ThrowsNullReferenceException_WhenContentIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new Memory(MemoryType.Note, null!));
    }

    [Theory]
    [InlineData(MemoryType.Fact)]
    [InlineData(MemoryType.Preference)]
    [InlineData(MemoryType.Decision)]
    [InlineData(MemoryType.Lesson)]
    [InlineData(MemoryType.Note)]
    public void Memory_SupportsAllMemoryTypes(MemoryType type)
    {
        var memory = new Memory(type, "test content");
        Assert.Equal(type, memory.Type);
    }

    [Theory]
    [InlineData(MemoryStatus.Active)]
    [InlineData(MemoryStatus.Superseded)]
    [InlineData(MemoryStatus.Archived)]
    public void Memory_SupportsAllMemoryStatuses(MemoryStatus status)
    {
        var memory = new Memory(MemoryType.Note, "test content", status: status);
        Assert.Equal(status, memory.Status);
    }
}
```

- [ ] **Step 2: Remove scaffold `UnitTest1.cs`**

- [ ] **Step 3: Run solution build & tests**

Run: `dotnet build Eling.slnx && dotnet test Eling.slnx`

- [ ] **Step 4: Commit**

```bash
git add tests/Eling.Core.Tests/MemoryTests.cs
git rm tests/Eling.Core.Tests/UnitTest1.cs
git commit -m "test(core): add unit tests for Memory domain model and MemoryId"
```
