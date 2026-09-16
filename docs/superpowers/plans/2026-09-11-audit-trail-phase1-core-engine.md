# Audit Trail v1 — Phase 1 (Core Engine) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the engine half of the audit trail — schema, a durable machine-global JSONL writer with an async batched flusher, daily rotation/compression, a rebuildable SQLite index, and retention/rollup — with no hooks into memory, runtime, or filesystem yet.

**Architecture:** The store is a single **machine-global** directory resolved by a new `CentralAuditDirectory` — a sibling of the existing `CentralLogDirectory` under `$XDG_DATA_HOME/eling` or `<userHome>/.local/share/eling`. Contracts and the file/SQLite implementations live in `Eling.Core.Audit` / `Eling.Core.Audit.Storage` / `Eling.Core.Audit.Retention`, mirroring the existing `Eling.Core.Memory` + `Eling.Core.Memory.Storage` split. A single `IAuditLogger` is fed into an in-process bounded `Channel`; a background flusher batches entries under a `SemaphoreSlim` and appends them to a per-trail, per-day JSONL file through an exclusive cross-process file handle. A separate SQLite index (a rebuildable cache) answers reads. No behavior is wired into production paths until Phase 2.

**Tech Stack:** .NET 10 (C# 14), `System.Threading.Channels`, `System.Text.Json` source generation, `Microsoft.Data.Sqlite` (already referenced by `Eling.Core` for `SqliteMemoryIndex`), xUnit 2.9.3.

**Design reference:** `docs/superpowers/specs/2026-09-11-audit-trail-design.md`.

## Global Constraints

- Target `net10.0`; `Nullable` and `ImplicitUsings` enabled.
- **One file = one type; the file name matches the type name.**
- **Namespace always syncs the folder** (`Eling.Core/Audit/Foo.cs` → `namespace Eling.Core.Audit;`).
- All exceptions live in `Eling.Core/Exceptions/`; do not define ad-hoc exception types inside feature folders.
- No absolute machine paths or usernames in tracked files. `ProjectRoot` is stored relative; the store root is resolved at runtime by `CentralAuditDirectory`.
- Audit writes are **fail-open**: a failed record must never fail the caller's operation.
- Tests run **per csproj**, never solution-wide: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test`.
- Use xUnit (`[Fact]`, `Assert.*`); async tests return `Task`.
- The store lives outside the repository (under the user data root), so no `.gitignore` change is required. Phase 1 works purely against temp directories in tests.

---

## File Structure

Create under `src/backend/Eling.Core/Audit/`:

- `AuditTrail.cs`, `AuditCategory.cs`, `AuditOutcome.cs` — enums.
- `AuditActor.cs`, `AuditActorContext.cs` — ambient actor.
- `AuditMetadata.cs`, `AuditEvent.cs`, `AuditJsonContext.cs` — schema + serialization.
- `IAuditLogger.cs`, `NullAuditLogger.cs`, `AuditOptions.cs` — contract.
- `Storage/AuditFileStore.cs`, `Storage/AuditRotation.cs` — JSONL append + rotation.
- `Storage/AuditRecord.cs`, `Storage/AuditQuery.cs`, `Storage/IAuditIndex.cs`, `Storage/SqliteAuditIndex.cs` — read index.
- `Storage/BufferedAuditLogger.cs` — async buffered writer.
- `Retention/AuditRollup.cs`, `Retention/AuditRetentionPolicy.cs`, `Retention/AuditMaintenanceJob.cs` — rollups/retention.

Create/modify under `src/backend/Eling.Core/Scope/`:

- `CentralAuditDirectory.cs` — machine-global audit root.
- Modify `CentralLogDirectory.cs` — factor out `ResolveRoot`.

Tests in `tests/Eling.Core.Tests/` (project root namespace `Eling.Core.Tests`).

---

### Task 0: Central audit directory resolution

**Files:**
- Modify: `src/backend/Eling.Core/Scope/CentralLogDirectory.cs`
- Create: `src/backend/Eling.Core/Scope/CentralAuditDirectory.cs`
- Test: `tests/Eling.Core.Tests/CentralAuditDirectoryTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `CentralLogDirectory.ResolveRoot(string? xdgDataHome = null, string? userHome = null)` → `<dataRoot>/eling`, creating the directory.
  - `CentralLogDirectory.Resolve(...)` — behaviour unchanged: `Path.Combine(ResolveRoot(...), DirectoryName)`.
  - `static class CentralAuditDirectory` with `const string DirectoryName = "audit"` and `Resolve(xdgDataHome = null, userHome = null)` → `Path.Combine(CentralLogDirectory.ResolveRoot(...), DirectoryName)`, creating the directory.

- [ ] **Step 1: Write the failing test**

```csharp
using Eling.Core.Scope;
using Xunit;

namespace Eling.Core.Tests;

public sealed class CentralAuditDirectoryTests
{
    [Fact]
    public void Resolve_IsSiblingOfLogsUnderElingRoot()
    {
        var home = Path.Combine(Path.GetTempPath(), $"eling-audit-dir-{Guid.NewGuid():N}");
        var expected = Path.Combine(home, ".local", "share", "eling", "audit");

        var actual = CentralAuditDirectory.Resolve(userHome: home);

        Assert.Equal(expected, actual);
        Assert.True(Directory.Exists(actual));
    }

    [Fact]
    public void Resolve_HonoursXdgDataHome()
    {
        var xdg = Path.Combine(Path.GetTempPath(), $"eling-audit-xdg-{Guid.NewGuid():N}");
        var expected = Path.Combine(xdg, "eling", "audit");

        var actual = CentralAuditDirectory.Resolve(xdgDataHome: xdg, userHome: "/home/user");

        Assert.Equal(expected, actual);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~CentralAuditDirectoryTests`
Expected: FAIL — `CentralAuditDirectory` does not exist.

- [ ] **Step 3: Write the minimal implementation**

Refactor `CentralLogDirectory.cs` to expose the root, keeping `Resolve` behaviour identical:
```csharp
namespace Eling.Core.Scope;

public static class CentralLogDirectory
{
    public const string DirectoryName = "logs"; // subdir under the eling root

    /// <summary>
    /// Resolves the Eling data root: <c>$XDG_DATA_HOME/eling</c> when set,
    /// otherwise <c>&lt;userHome&gt;/.local/share/eling</c>, on every platform
    /// (Windows included; %LOCALAPPDATA% is intentionally not used).
    /// </summary>
    public static string ResolveRoot(string? xdgDataHome = null, string? userHome = null)
    {
        var envXdg = !string.IsNullOrWhiteSpace(xdgDataHome)
            ? xdgDataHome
            : Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        var home = string.IsNullOrWhiteSpace(userHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userHome;
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        var dataRoot = !string.IsNullOrWhiteSpace(envXdg)
            ? envXdg!
            : Path.Combine(home, ".local", "share");

        var root = Path.Combine(dataRoot, "eling");
        try
        {
            Directory.CreateDirectory(root);
        }
        catch
        {
        }
        return root;
    }

    public static string Resolve(string? xdgDataHome = null, string? userHome = null)
    {
        var path = Path.Combine(ResolveRoot(xdgDataHome, userHome), DirectoryName);
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
        }
        return path;
    }
}
```

`CentralAuditDirectory.cs`:
```csharp
namespace Eling.Core.Scope;

public static class CentralAuditDirectory
{
    public const string DirectoryName = "audit";

    /// <summary>
    /// Resolves the central, machine-global audit store beside the log directory.
    /// It is global on purpose: entries carry their originating <c>scope</c> and
    /// <c>projectRoot</c> as fields, so one store serves every project.
    /// </summary>
    public static string Resolve(string? xdgDataHome = null, string? userHome = null)
    {
        var path = Path.Combine(CentralLogDirectory.ResolveRoot(xdgDataHome, userHome), DirectoryName);
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
        }
        return path;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~CentralAuditDirectoryTests`
Expected: PASS (2 tests). Also re-run `CentralLogDirectoryTests` to confirm the refactor kept its behaviour: `--filter FullyQualifiedName~CentralLogDirectoryTests` → PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Scope tests/Eling.Core.Tests/CentralAuditDirectoryTests.cs
git commit -m "feat(audit): add central audit directory resolution"
```

---

### Task 1: Audit enums and actor context

**Files:**
- Create: `src/backend/Eling.Core/Audit/AuditTrail.cs`
- Create: `src/backend/Eling.Core/Audit/AuditCategory.cs`
- Create: `src/backend/Eling.Core/Audit/AuditOutcome.cs`
- Create: `src/backend/Eling.Core/Audit/AuditActor.cs`
- Create: `src/backend/Eling.Core/Audit/AuditActorContext.cs`
- Test: `tests/Eling.Core.Tests/AuditActorContextTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `enum AuditTrail { Change, Access }`
  - `enum AuditCategory { Memory, Runtime, Filesystem }`
  - `enum AuditOutcome { Success, Error, Denied }`
  - `record AuditActor(string Actor, string Source)` with `static AuditActor Unknown` and `static AuditActor System`
  - `static class AuditActorContext` with `AuditActor Current { get; }` and `IDisposable Begin(AuditActor actor)`

- [ ] **Step 1: Write the failing test**

```csharp
using Eling.Core.Audit;
using Xunit;

namespace Eling.Core.Tests;

public class AuditActorContextTests
{
    [Fact]
    public void Current_WhenUnset_IsUnknown()
    {
        Assert.Equal("unknown", AuditActorContext.Current.Actor);
    }

    [Fact]
    public async Task Begin_ScopesActorToTheAsyncFlow()
    {
        AuditActor? seenInside = null;

        using (AuditActorContext.Begin(new AuditActor("mcp:eling_dev", "mcp_stdio")))
        {
            await Task.Yield();
            seenInside = AuditActorContext.Current;
        }

        Assert.Equal("mcp:eling_dev", seenInside!.Actor);
        Assert.Equal("unknown", AuditActorContext.Current.Actor);
    }

    [Fact]
    public async Task Begin_DoesNotLeakAcrossSiblingTasks()
    {
        var other = await Task.Run(() => AuditActorContext.Current.Actor);

        using (AuditActorContext.Begin(new AuditActor("dashboard", "dashboard_web")))
        {
            Assert.Equal("dashboard", AuditActorContext.Current.Actor);
        }

        Assert.Equal("unknown", other);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~AuditActorContextTests`
Expected: FAIL — `AuditActorContext` does not exist.

- [ ] **Step 3: Write the minimal implementation**

`AuditTrail.cs`:
```csharp
namespace Eling.Core.Audit;

public enum AuditTrail
{
    Change,
    Access
}
```

`AuditCategory.cs`:
```csharp
namespace Eling.Core.Audit;

public enum AuditCategory
{
    Memory,
    Runtime,
    Filesystem
}
```

`AuditOutcome.cs`:
```csharp
namespace Eling.Core.Audit;

public enum AuditOutcome
{
    Success,
    Error,
    Denied
}
```

`AuditActor.cs`:
```csharp
namespace Eling.Core.Audit;

public sealed record AuditActor(string Actor, string Source)
{
    public static readonly AuditActor Unknown = new("unknown", "system");
    public static readonly AuditActor System = new("system", "system");
}
```

`AuditActorContext.cs`:
```csharp
namespace Eling.Core.Audit;

public static class AuditActorContext
{
    private static readonly AsyncLocal<AuditActor?> _current = new();

    public static AuditActor Current => _current.Value ?? AuditActor.Unknown;

    public static IDisposable Begin(AuditActor actor)
    {
        var previous = _current.Value;
        _current.Value = actor;
        return new Scope(previous);
    }

    private sealed class Scope(AuditActor? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~AuditActorContextTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Audit tests/Eling.Core.Tests/AuditActorContextTests.cs
git commit -m "feat(audit): add audit enums and async actor context"
```

---

### Task 2: Audit entry schema and JSON serialization

**Files:**
- Create: `src/backend/Eling.Core/Audit/AuditMetadata.cs`
- Create: `src/backend/Eling.Core/Audit/AuditEvent.cs`
- Create: `src/backend/Eling.Core/Audit/AuditJsonContext.cs`
- Test: `tests/Eling.Core.Tests/AuditEventSerializationTests.cs`

**Interfaces:**
- Consumes: `AuditTrail`, `AuditCategory`, `AuditOutcome` (Task 1); `Eling.Core.Memory.MemoryScopeKind`.
- Produces:
  - `sealed class AuditMetadata` (a state snapshot) with nullable properties `Content`, `Tags`, `Status`, `MemorySource`.
  - `sealed class AuditEvent` with `int SchemaVersion`, `DateTimeOffset Timestamp`, `AuditTrail Trail`, `AuditCategory Category`, `string Action`, `string Actor`, `string Source`, `MemoryScopeKind? Scope`, `string? ProjectRoot`, `string? Target`, `AuditOutcome Outcome`, `int? DurationMs`, `long? ByteSize`, `string? Hash`, `string? PrevHash`, `string? Query`, `int? ResultCount`, `string? Path`, `int? Matches`, `int? Pid`, `AuditMetadata? Metadata`, `AuditMetadata? PreviousMetadata`.
  - `partial class AuditJsonContext : JsonSerializerContext` with camelCase, string enums, and null-field omission (`WhenWritingNull`).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using Eling.Core.Audit;
using Eling.Core.Memory;
using Xunit;

namespace Eling.Core.Tests;

public class AuditEventSerializationTests
{
    [Fact]
    public void Serialize_ChangeEntry_CarriesStateSnapshotsAndOmitsNulls()
    {
        var entry = new AuditEvent
        {
            Timestamp = new DateTimeOffset(2026, 9, 11, 9, 30, 0, TimeSpan.Zero),
            Trail = AuditTrail.Change,
            Category = AuditCategory.Memory,
            Action = "memory_update",
            Actor = "mcp:eling_dev",
            Source = "mcp_stdio",
            Scope = MemoryScopeKind.Project,
            Target = "01m0000000000000000000000",
            Metadata = new AuditMetadata { Content = "hello", Tags = ["audit"], Status = "active" },
            PreviousMetadata = new AuditMetadata { Content = "old" }
        };

        var json = JsonSerializer.Serialize(entry, AuditJsonContext.Default.AuditEvent);

        Assert.DoesNotContain("\n", json);
        Assert.Contains("\"trail\":\"change\"", json);
        Assert.Contains("\"category\":\"memory\"", json);
        Assert.Contains("\"scope\":\"project\"", json);
        Assert.Contains("\"previousMetadata\":", json);
        Assert.DoesNotContain("\"hash\"", json); // null fields are omitted on disk
    }

    [Fact]
    public void Serialize_AccessEntry_KeepsDetailFlatAndOmitsState()
    {
        var entry = new AuditEvent
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            Trail = AuditTrail.Access,
            Category = AuditCategory.Filesystem,
            Action = "fs_read",
            Actor = "dashboard",
            Source = "dashboard_web",
            Target = "src/a.cs",
            Path = "src/a.cs",
            ByteSize = 128
        };

        var json = JsonSerializer.Serialize(entry, AuditJsonContext.Default.AuditEvent);
        var back = JsonSerializer.Deserialize(json, AuditJsonContext.Default.AuditEvent);

        Assert.Equal(1, back!.SchemaVersion);
        Assert.Equal(AuditTrail.Access, back.Trail);
        Assert.Equal("fs_read", back.Action);
        Assert.Equal("src/a.cs", back.Path);
        Assert.Equal(128, back.ByteSize);
        Assert.Null(back.Metadata);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~AuditEventSerializationTests`
Expected: FAIL — `AuditEvent` / `AuditJsonContext` do not exist.

- [ ] **Step 3: Write the minimal implementation**

`AuditMetadata.cs`:
```csharp
namespace Eling.Core.Audit;

public sealed class AuditMetadata
{
    public string? Content { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Status { get; init; }
    public string? MemorySource { get; init; }
}
```

`AuditEvent.cs`:
```csharp
using Eling.Core.Memory;

namespace Eling.Core.Audit;

public sealed class AuditEvent
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset Timestamp { get; init; }
    public AuditTrail Trail { get; init; }
    public AuditCategory Category { get; init; }
    public string Action { get; init; } = string.Empty;
    public string Actor { get; init; } = AuditActor.Unknown.Actor;
    public string Source { get; init; } = AuditActor.Unknown.Source;
    public MemoryScopeKind? Scope { get; init; }
    public string? ProjectRoot { get; init; }
    public string? Target { get; init; }
    public AuditOutcome Outcome { get; init; } = AuditOutcome.Success;
    public int? DurationMs { get; init; }
    public long? ByteSize { get; init; }
    public string? Hash { get; init; }
    public string? PrevHash { get; init; }
    // Access-only detail; null for other entries.
    public string? Query { get; init; }
    public int? ResultCount { get; init; }
    public string? Path { get; init; }
    public int? Matches { get; init; }
    public int? Pid { get; init; }
    // Change-trail state snapshots.
    public AuditMetadata? Metadata { get; init; }
    public AuditMetadata? PreviousMetadata { get; init; }
}
```

`AuditJsonContext.cs`:
```csharp
using System.Text.Json.Serialization;

namespace Eling.Core.Audit;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AuditEvent))]
public partial class AuditJsonContext : JsonSerializerContext
{
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~AuditEventSerializationTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Audit tests/Eling.Core.Tests/AuditEventSerializationTests.cs
git commit -m "feat(audit): add audit entry schema and JSON context"
```

---

### Task 3: Logger contract, no-op logger, and options

**Files:**
- Create: `src/backend/Eling.Core/Audit/IAuditLogger.cs`
- Create: `src/backend/Eling.Core/Audit/NullAuditLogger.cs`
- Create: `src/backend/Eling.Core/Audit/AuditOptions.cs`
- Test: `tests/Eling.Core.Tests/NullAuditLoggerTests.cs`

**Interfaces:**
- Consumes: `AuditEvent` (Task 2).
- Produces:
  - `interface IAuditLogger { ValueTask RecordAsync(AuditEvent entry, CancellationToken cancellationToken = default); }`
  - `sealed class NullAuditLogger : IAuditLogger` (singleton `Instance`)
  - `sealed class AuditOptions` with `int ChangeRetentionDays = 365`, `int AccessRetentionDays = 14`, `int FlushBatchSize = 128`, `TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250)`, `TimeSpan FlushTimeout = TimeSpan.FromSeconds(2)`, `bool CompressOnRotate = true`, `bool CaptureQueryText = true`.

- [ ] **Step 1: Write the failing test**

```csharp
using Eling.Core.Audit;
using Xunit;

namespace Eling.Core.Tests;

public class NullAuditLoggerTests
{
    [Fact]
    public async Task RecordAsync_CompletesWithoutThrowing()
    {
        IAuditLogger logger = NullAuditLogger.Instance;
        await logger.RecordAsync(new AuditEvent { Action = "memory_read" });
        Assert.Same(NullAuditLogger.Instance, logger);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~NullAuditLoggerTests`
Expected: FAIL — `IAuditLogger` does not exist.

- [ ] **Step 3: Write the minimal implementation**

`IAuditLogger.cs`:
```csharp
namespace Eling.Core.Audit;

public interface IAuditLogger
{
    ValueTask RecordAsync(AuditEvent entry, CancellationToken cancellationToken = default);
}
```

`NullAuditLogger.cs`:
```csharp
namespace Eling.Core.Audit;

public sealed class NullAuditLogger : IAuditLogger
{
    public static readonly NullAuditLogger Instance = new();

    private NullAuditLogger()
    {
    }

    public ValueTask RecordAsync(AuditEvent entry, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
```

`AuditOptions.cs`:
```csharp
namespace Eling.Core.Audit;

public sealed class AuditOptions
{
    public int ChangeRetentionDays { get; init; } = 365;
    public int AccessRetentionDays { get; init; } = 14;
    public int FlushBatchSize { get; init; } = 128;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan FlushTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public bool CompressOnRotate { get; init; } = true;
    public bool CaptureQueryText { get; init; } = true;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~NullAuditLoggerTests`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Audit tests/Eling.Core.Tests/NullAuditLoggerTests.cs
git commit -m "feat(audit): add logger contract, no-op logger, and options"
```

---

### Task 4: File store with cross-process exclusive append

**Files:**
- Create: `src/backend/Eling.Core/Audit/Storage/AuditFileStore.cs`
- Create: `src/backend/Eling.Core/Audit/Storage/AuditAppendException.cs` (only if an exception type is genuinely needed; otherwise reuse `IOException`)
- Test: `tests/Eling.Core.Tests/AuditFileStoreTests.cs`

**Interfaces:**
- Consumes: `AuditTrail` (Task 1), `AuditEvent`/`AuditJsonContext` (Task 2), `AuditOptions` (Task 3).
- Produces:
  - `sealed class AuditFileStore(string auditRoot, TimeProvider timeProvider)` — `auditRoot` comes from `CentralAuditDirectory.Resolve()` in production (Task 0) and a temp directory in tests.
  - `string GetCurrentFilePath(AuditTrail trail, DateTimeOffset now)` → `<auditRoot>/{changes|access}/{yyyy-MM-dd}.jsonl`
  - `Task AppendAsync(AuditTrail trail, IReadOnlyList<AuditEvent> entries, bool flushToDisk, CancellationToken cancellationToken)`
  - Behaviour: opens `FileMode.Append, FileAccess.Write, FileShare.Read`; retries `IOException` up to 5 times with 20ms×attempt backoff; writes each entry as one line + `\n`; when `flushToDisk` is true calls `stream.Flush(flushToDisk: true)`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using Eling.Core.Audit;
using Eling.Core.Audit.Storage;
using Xunit;

namespace Eling.Core.Tests;

public class AuditFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eling-audit-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AppendAsync_WritesOneJsonLinePerEntry()
    {
        var store = new AuditFileStore(_root, TimeProvider.System);
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        var entries = new[]
        {
            new AuditEvent { Timestamp = now, Trail = AuditTrail.Change, Category = AuditCategory.Memory, Action = "memory_save" },
            new AuditEvent { Timestamp = now.AddSeconds(1), Trail = AuditTrail.Change, Category = AuditCategory.Memory, Action = "memory_delete" }
        };

        await store.AppendAsync(AuditTrail.Change, entries, flushToDisk: true, CancellationToken.None);

        var path = store.GetCurrentFilePath(AuditTrail.Change, now);
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal("memory_save", JsonSerializer.Deserialize(lines[0], AuditJsonContext.Default.AuditEvent)!.Action);
    }

    [Fact]
    public async Task AppendAsync_TwoConcurrentStores_ProduceNoInterleavedLines()
    {
        var a = new AuditFileStore(_root, TimeProvider.System);
        var b = new AuditFileStore(_root, TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        var batches = Enumerable.Range(0, 40)
            .Select(i => Task.Run(() => (i % 2 == 0 ? a : b).AppendAsync(
                AuditTrail.Change,
                [new AuditEvent { Timestamp = now, Trail = AuditTrail.Change, Category = AuditCategory.Runtime, Action = "runtime_register" }],
                flushToDisk: false,
                CancellationToken.None)))
            .ToArray();

        await Task.WhenAll(batches);

        var lines = await File.ReadAllLinesAsync(a.GetCurrentFilePath(AuditTrail.Change, now));
        Assert.Equal(40, lines.Length);
        foreach (var line in lines)
        {
            Assert.NotNull(JsonSerializer.Deserialize(line, AuditJsonContext.Default.AuditEvent));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~AuditFileStoreTests`
Expected: FAIL — `AuditFileStore` does not exist.

- [ ] **Step 3: Write the minimal implementation**

```csharp
using System.Text.Json;

namespace Eling.Core.Audit.Storage;

/// <summary>
/// Appends audit entries to per-trail, per-day JSONL files. Cross-process
/// exclusivity comes from opening the file for append with a write-denying
/// share mode; a sharing violation is retried with bounded backoff. The OS
/// releases the handle when a process dies, so no stale lock can survive.
/// </summary>
public sealed class AuditFileStore(string auditRoot, TimeProvider timeProvider)
{
    private const int MaxAttempts = 5;

    public string GetCurrentFilePath(AuditTrail trail, DateTimeOffset now)
    {
        var dir = Path.Combine(auditRoot, trail == AuditTrail.Change ? "changes" : "access");
        return Path.Combine(dir, $"{now:yyyy-MM-dd}.jsonl");
    }

    public async Task AppendAsync(
        AuditTrail trail,
        IReadOnlyList<AuditEvent> entries,
        bool flushToDisk,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return;

        var now = timeProvider.GetUtcNow();
        var path = GetCurrentFilePath(trail, now);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var payload = string.Concat(entries.Select(e =>
            JsonSerializer.Serialize(e, AuditJsonContext.Default.AuditEvent) + "\n"));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);

                var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                await stream.WriteAsync(bytes, cancellationToken);
                if (flushToDisk)
                {
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                return;
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                await Task.Delay(20 * attempt, cancellationToken);
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~AuditFileStoreTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Audit tests/Eling.Core.Tests/AuditFileStoreTests.cs
git commit -m "feat(audit): add jsonl file store with cross-process append"
```

---

### Task 5: Daily rotation and compression

**Files:**
- Modify: `src/backend/Eling.Core/Audit/Storage/AuditFileStore.cs`
- Create: `src/backend/Eling.Core/Audit/Storage/AuditRotation.cs`
- Test: `tests/Eling.Core.Tests/AuditRotationTests.cs`

**Interfaces:**
- Consumes: `AuditFileStore` (Task 4), `AuditOptions` (Task 3), `TimeProvider`.
- Produces:
  - `static class AuditRotation` with `Task<string> RotateIfNeededAsync(AuditFileStore store, AuditTrail trail, DateTimeOffset now, bool compress, CancellationToken ct)` returning the current file path.
  - Rotation rule: if the newest `*.jsonl` for the trail is not the file for `now`'s date, gzip it to `<name>.jsonl.gz` and delete the original; never touch the file for `now`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO.Compression;
using Eling.Core.Audit;
using Eling.Core.Audit.Storage;
using Xunit;

namespace Eling.Core.Tests;

public class AuditRotationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eling-audit-rot-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RotateIfNeeded_CompressesPreviousDayAndKeepsCurrent()
    {
        var store = new AuditFileStore(_root, TimeProvider.System);
        var day1 = new DateTimeOffset(2026, 9, 10, 23, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddHours(2);

        await store.AppendAsync(AuditTrail.Change, [new AuditEvent { Timestamp = day1, Action = "memory_save" }], true, CancellationToken.None);

        var rotated = await AuditRotation.RotateIfNeededAsync(store, AuditTrail.Change, day2, compress: true, CancellationToken.None);

        var dir = Path.Combine(_root, "changes");
        Assert.True(File.Exists(Path.Combine(dir, "2026-09-10.jsonl.gz")));
        Assert.False(File.Exists(Path.Combine(dir, "2026-09-10.jsonl")));
        Assert.EndsWith("2026-09-11.jsonl", rotated);

        await using var gz = File.OpenRead(Path.Combine(dir, "2026-09-10.jsonl.gz"));
        using var gunzip = new GZipStream(gz, CompressionMode.Decompress);
        using var reader = new StreamReader(gunzip);
        Assert.Contains("memory_save", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task RotateIfNeeded_WhenSameDay_IsNoOp()
    {
        var store = new AuditFileStore(_root, TimeProvider.System);
        var day = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        await store.AppendAsync(AuditTrail.Change, [new AuditEvent { Timestamp = day, Action = "memory_save" }], true, CancellationToken.None);

        var rotated = await AuditRotation.RotateIfNeededAsync(store, AuditTrail.Change, day.AddMinutes(30), compress: true, CancellationToken.None);

        Assert.True(File.Exists(rotated));
        Assert.False(File.Exists(Path.Combine(_root, "changes", "2026-09-11.jsonl.gz")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~AuditRotationTests`
Expected: FAIL — `AuditRotation` does not exist.

- [ ] **Step 3: Write the minimal implementation**

`AuditRotation.cs`:
```csharp
using System.IO.Compression;

namespace Eling.Core.Audit.Storage;

public static class AuditRotation
{
    /// <summary>
    /// Lazy, lock-internal rotation. Any day file other than <paramref name="now"/>'s
    /// date is compressed and removed; the current day file is left untouched.
    /// No background timer is used, so the behaviour is deterministic in tests.
    /// </summary>
    public static async Task<string> RotateIfNeededAsync(
        AuditFileStore store,
        AuditTrail trail,
        DateTimeOffset now,
        bool compress,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(store.GetCurrentFilePath(trail, now))!;
        var current = Path.GetFileName(store.GetCurrentFilePath(trail, now));
        if (!Directory.Exists(dir)) return store.GetCurrentFilePath(trail, now);

        foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
        {
            if (string.Equals(Path.GetFileName(file), current, StringComparison.Ordinal)) continue;

            if (compress)
            {
                var target = file + ".gz";
                await using (var input = File.OpenRead(file))
                await using (var output = File.Create(target))
                await using (var gz = new GZipStream(output, CompressionLevel.SmallestSize))
                {
                    await input.CopyToAsync(gz, cancellationToken);
                }
                File.Delete(file);
            }
        }

        return store.GetCurrentFilePath(trail, now);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~AuditRotationTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Audit tests/Eling.Core.Tests/AuditRotationTests.cs
git commit -m "feat(audit): add lazy daily rotation with gzip"
```

---

### Task 6: SQLite audit index (rebuildable cache)

**Files:**
- Create: `src/backend/Eling.Core/Audit/Storage/AuditRecord.cs`
- Create: `src/backend/Eling.Core/Audit/Storage/AuditQuery.cs`
- Create: `src/backend/Eling.Core/Audit/Storage/IAuditIndex.cs`
- Create: `src/backend/Eling.Core/Audit/Storage/SqliteAuditIndex.cs`
- Test: `tests/Eling.Core.Tests/SqliteAuditIndexTests.cs`

**Interfaces:**
- Consumes: `AuditEvent`, `AuditTrail`, `AuditCategory` (Tasks 1–2).
- Produces:
  - `sealed record AuditRecord(string File, int Line, AuditEvent Event)`
  - `sealed record AuditQuery(AuditTrail Trail, DateTimeOffset? From, DateTimeOffset? To, string? Action = null, string? Actor = null, int Limit = 200)`
  - `interface IAuditIndex` with `Task InitializeAsync(CancellationToken)`, `Task IndexAsync(IReadOnlyList<AuditRecord> records, CancellationToken)`, `Task RebuildAsync(CancellationToken)`, `Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken)`.
  - `sealed class SqliteAuditIndex(string auditRoot, AuditFileStore store)` — normalized into three tables (`audit_events`, `audit_state`, `audit_tags`); no JSON payload column.

- [ ] **Step 1: Write the failing test**

```csharp
using Eling.Core.Audit;
using Eling.Core.Audit.Storage;
using Xunit;

namespace Eling.Core.Tests;

public class SqliteAuditIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eling-audit-idx-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task QueryAsync_FiltersByActionAndOrdersDescending()
    {
        var store = new AuditFileStore(_root, TimeProvider.System);
        var index = new SqliteAuditIndex(_root, store);
        await index.InitializeAsync(CancellationToken.None);

        var t0 = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        await index.IndexAsync(
        [
            new AuditRecord("changes/2026-09-11.jsonl", 1, new AuditEvent { Timestamp = t0, Trail = AuditTrail.Change, Action = "memory_save" }),
            new AuditRecord("changes/2026-09-11.jsonl", 2, new AuditEvent { Timestamp = t0.AddMinutes(1), Trail = AuditTrail.Change, Action = "memory_delete" }),
            new AuditRecord("changes/2026-09-11.jsonl", 3, new AuditEvent { Timestamp = t0.AddMinutes(2), Trail = AuditTrail.Change, Action = "memory_save" })
        ], CancellationToken.None);

        var result = await index.QueryAsync(new AuditQuery(AuditTrail.Change, null, null, Action: "memory_save"), CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.True(result[0].Event.Timestamp > result[1].Event.Timestamp);
    }

    [Fact]
    public async Task RebuildAsync_ReconstructsIndexFromJsonl()
    {
        var store = new AuditFileStore(_root, TimeProvider.System);
        var day = new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);
        await store.AppendAsync(
            AuditTrail.Change,
            [new AuditEvent { Timestamp = day, Trail = AuditTrail.Change, Action = "memory_save" }],
            flushToDisk: true,
            CancellationToken.None);

        var index = new SqliteAuditIndex(_root, store);
        await index.RebuildAsync(CancellationToken.None);

        var result = await index.QueryAsync(new AuditQuery(AuditTrail.Change, null, null), CancellationToken.None);
        Assert.Single(result);
    }

    [Fact]
    public async Task IndexAsync_PersistsStateAndTagsAcrossTheThreeTables()
    {
        var store = new AuditFileStore(_root, TimeProvider.System);
        var index = new SqliteAuditIndex(_root, store);
        await index.InitializeAsync(CancellationToken.None);

        var t0 = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        await index.IndexAsync(
        [
            new AuditRecord("changes/2026-09-11.jsonl", 1, new AuditEvent
            {
                Timestamp = t0,
                Trail = AuditTrail.Change,
                Category = AuditCategory.Memory,
                Action = "memory_update",
                Metadata = new AuditMetadata { Content = "after", Tags = ["a", "b"], Status = "active", MemorySource = "chat" },
                PreviousMetadata = new AuditMetadata { Content = "before", Tags = ["c"] }
            })
        ], CancellationToken.None);

        var entry = (await index.QueryAsync(new AuditQuery(AuditTrail.Change, null, null), CancellationToken.None)).Single().Event;

        Assert.Equal("after", entry.Metadata!.Content);
        Assert.Equal(new[] { "a", "b" }, entry.Metadata.Tags);
        Assert.Equal("chat", entry.Metadata.MemorySource);
        Assert.Equal("before", entry.PreviousMetadata!.Content);
        Assert.Equal(new[] { "c" }, entry.PreviousMetadata.Tags);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~SqliteAuditIndexTests`
Expected: FAIL — `SqliteAuditIndex` does not exist.

- [ ] **Step 3: Write the minimal implementation**

`AuditRecord.cs`:
```csharp
namespace Eling.Core.Audit.Storage;

public sealed record AuditRecord(string File, int Line, AuditEvent Event);
```

`AuditQuery.cs`:
```csharp
namespace Eling.Core.Audit.Storage;

public sealed record AuditQuery(
    AuditTrail Trail,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Action = null,
    string? Actor = null,
    int Limit = 200);
```

`IAuditIndex.cs`:
```csharp
namespace Eling.Core.Audit.Storage;

public interface IAuditIndex
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task IndexAsync(IReadOnlyList<AuditRecord> records, CancellationToken cancellationToken);
    Task RebuildAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken cancellationToken);
}
```

`SqliteAuditIndex.cs`:
```csharp
using System.Text.Json;
using Eling.Core.Memory;
using Microsoft.Data.Sqlite;

namespace Eling.Core.Audit.Storage;

/// <summary>
/// Rebuildable read cache over the JSONL files, normalized into three relational
/// tables (audit_events, audit_state, audit_tags) with no JSON payload column.
/// Losing this database is safe: <see cref="RebuildAsync"/> rescans every JSONL
/// file and repopulates it, the same cache-not-truth contract as the memory index.
/// </summary>
public sealed class SqliteAuditIndex(string auditRoot, AuditFileStore store) : IAuditIndex
{
    private const string After = "after";
    private const string Previous = "previous";

    private string DbPath => Path.Combine(auditRoot, "audit-index.db");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(auditRoot);
        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS audit_events (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp    TEXT NOT NULL,
                trail        TEXT NOT NULL,
                category     TEXT NOT NULL,
                action       TEXT NOT NULL,
                actor        TEXT NOT NULL,
                source       TEXT NOT NULL,
                scope        TEXT NULL,
                project_root TEXT NULL,
                target       TEXT NULL,
                outcome      TEXT NOT NULL,
                duration_ms  INTEGER NULL,
                byte_size    INTEGER NULL,
                hash         TEXT NULL,
                prev_hash    TEXT NULL,
                query_text   TEXT NULL,
                result_count INTEGER NULL,
                path         TEXT NULL,
                matches      INTEGER NULL,
                pid          INTEGER NULL,
                file         TEXT NOT NULL,
                line         INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_audit_events_timestamp ON audit_events(timestamp);
            CREATE INDEX IF NOT EXISTS ix_audit_events_action ON audit_events(action);
            CREATE INDEX IF NOT EXISTS ix_audit_events_actor ON audit_events(actor);

            CREATE TABLE IF NOT EXISTS audit_state (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id      INTEGER NOT NULL REFERENCES audit_events(id) ON DELETE CASCADE,
                side          TEXT NOT NULL,
                content       TEXT NULL,
                status        TEXT NULL,
                memory_source TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_audit_state_event ON audit_state(event_id);

            CREATE TABLE IF NOT EXISTS audit_tags (
                state_id INTEGER NOT NULL REFERENCES audit_state(id) ON DELETE CASCADE,
                ordinal  INTEGER NOT NULL,
                tag      TEXT NOT NULL,
                PRIMARY KEY (state_id, ordinal)
            );
            """, cancellationToken);
    }

    public async Task IndexAsync(IReadOnlyList<AuditRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0) return;
        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var record in records)
        {
            var eventId = await InsertEventAsync(connection, transaction, record, cancellationToken);
            await InsertStateAsync(connection, transaction, eventId, After, record.Event.Metadata, cancellationToken);
            await InsertStateAsync(connection, transaction, eventId, Previous, record.Event.PreviousMetadata, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<long> InsertEventAsync(
        SqliteConnection connection, SqliteTransaction transaction, AuditRecord record, CancellationToken cancellationToken)
    {
        var e = record.Event;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_events
                (timestamp, trail, category, action, actor, source, scope, project_root, target,
                 outcome, duration_ms, byte_size, hash, prev_hash,
                 query_text, result_count, path, matches, pid, file, line)
            VALUES
                ($ts, $trail, $cat, $action, $actor, $source, $scope, $root, $target,
                 $outcome, $duration, $size, $hash, $prevhash,
                 $query, $count, $path, $matches, $pid, $file, $line);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$ts", e.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$trail", e.Trail.ToString());
        command.Parameters.AddWithValue("$cat", e.Category.ToString());
        command.Parameters.AddWithValue("$action", e.Action);
        command.Parameters.AddWithValue("$actor", e.Actor);
        command.Parameters.AddWithValue("$source", e.Source);
        command.Parameters.AddWithValue("$scope", (object?)e.Scope?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$root", (object?)e.ProjectRoot ?? DBNull.Value);
        command.Parameters.AddWithValue("$target", (object?)e.Target ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", e.Outcome.ToString());
        command.Parameters.AddWithValue("$duration", (object?)e.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", (object?)e.ByteSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", (object?)e.Hash ?? DBNull.Value);
        command.Parameters.AddWithValue("$prevhash", (object?)e.PrevHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$query", (object?)e.Query ?? DBNull.Value);
        command.Parameters.AddWithValue("$count", (object?)e.ResultCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", (object?)e.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$matches", (object?)e.Matches ?? DBNull.Value);
        command.Parameters.AddWithValue("$pid", (object?)e.Pid ?? DBNull.Value);
        command.Parameters.AddWithValue("$file", record.File);
        command.Parameters.AddWithValue("$line", record.Line);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertStateAsync(
        SqliteConnection connection, SqliteTransaction transaction, long eventId,
        string side, AuditMetadata? state, CancellationToken cancellationToken)
    {
        if (state is null) return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_state (event_id, side, content, status, memory_source)
            VALUES ($event, $side, $content, $status, $source);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$side", side);
        command.Parameters.AddWithValue("$content", (object?)state.Content ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (object?)state.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", (object?)state.MemorySource ?? DBNull.Value);
        var stateId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));

        if (state.Tags is not { Count: > 0 }) return;
        for (var ordinal = 0; ordinal < state.Tags.Count; ordinal++)
        {
            await using var tagCommand = connection.CreateCommand();
            tagCommand.Transaction = transaction;
            tagCommand.CommandText = "INSERT INTO audit_tags (state_id, ordinal, tag) VALUES ($state, $ord, $tag);";
            tagCommand.Parameters.AddWithValue("$state", stateId);
            tagCommand.Parameters.AddWithValue("$ord", ordinal);
            tagCommand.Parameters.AddWithValue("$tag", state.Tags[ordinal]);
            await tagCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task RebuildAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(connection, "DELETE FROM audit_tags; DELETE FROM audit_state; DELETE FROM audit_events;", cancellationToken);
        }

        var records = new List<AuditRecord>();
        foreach (var trail in new[] { AuditTrail.Change, AuditTrail.Access })
        {
            var dir = Path.Combine(auditRoot, trail == AuditTrail.Change ? "changes" : "access");
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
            {
                var lineNumber = 0;
                foreach (var line in await File.ReadAllLinesAsync(file, cancellationToken))
                {
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entry = JsonSerializer.Deserialize(line, AuditJsonContext.Default.AuditEvent);
                    if (entry is null) continue;
                    records.Add(new AuditRecord(Path.GetFileName(file), lineNumber, entry));
                }
            }
        }

        // Chunk to keep the write transaction bounded.
        foreach (var chunk in records.Chunk(500))
        {
            await IndexAsync(chunk, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        var rows = new List<(long Id, string File, int Line, AuditEvent Event)>();
        await using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, file, line, timestamp, trail, category, action, actor, source, scope,
                       project_root, target, outcome, duration_ms, byte_size, hash, prev_hash,
                       query_text, result_count, path, matches, pid
                FROM audit_events
                WHERE trail = $trail
                  AND ($action IS NULL OR action = $action)
                  AND ($actor IS NULL OR actor = $actor)
                  AND ($from IS NULL OR timestamp >= $from)
                  AND ($to IS NULL OR timestamp <= $to)
                ORDER BY timestamp DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$trail", query.Trail.ToString());
            command.Parameters.AddWithValue("$action", (object?)query.Action ?? DBNull.Value);
            command.Parameters.AddWithValue("$actor", (object?)query.Actor ?? DBNull.Value);
            command.Parameters.AddWithValue("$from", (object?)query.From?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$to", (object?)query.To?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", query.Limit);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    new AuditEvent
                    {
                        Timestamp = DateTimeOffset.Parse(reader.GetString(3)),
                        Trail = Enum.Parse<AuditTrail>(reader.GetString(4)),
                        Category = Enum.Parse<AuditCategory>(reader.GetString(5)),
                        Action = reader.GetString(6),
                        Actor = reader.GetString(7),
                        Source = reader.GetString(8),
                        Scope = reader.IsDBNull(9) ? null : Enum.Parse<MemoryScopeKind>(reader.GetString(9)),
                        ProjectRoot = reader.IsDBNull(10) ? null : reader.GetString(10),
                        Target = reader.IsDBNull(11) ? null : reader.GetString(11),
                        Outcome = Enum.Parse<AuditOutcome>(reader.GetString(12)),
                        DurationMs = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                        ByteSize = reader.IsDBNull(14) ? null : reader.GetInt64(14),
                        Hash = reader.IsDBNull(15) ? null : reader.GetString(15),
                        PrevHash = reader.IsDBNull(16) ? null : reader.GetString(16),
                        Query = reader.IsDBNull(17) ? null : reader.GetString(17),
                        ResultCount = reader.IsDBNull(18) ? null : reader.GetInt32(18),
                        Path = reader.IsDBNull(19) ? null : reader.GetString(19),
                        Matches = reader.IsDBNull(20) ? null : reader.GetInt32(20),
                        Pid = reader.IsDBNull(21) ? null : reader.GetInt32(21)
                    }));
            }
        }

        if (rows.Count == 0) return Array.Empty<AuditRecord>();

        var states = await LoadStatesAsync(rows.Select(r => r.Id).ToArray(), cancellationToken);

        var results = new List<AuditRecord>(rows.Count);
        foreach (var (id, file, line, entry) in rows)
        {
            states.TryGetValue(id, out var pair);
            results.Add(new AuditRecord(file, line, WithState(entry, pair.After, pair.Previous)));
        }
        return results;
    }

    private async Task<Dictionary<long, (AuditMetadata? After, AuditMetadata? Previous)>> LoadStatesAsync(
        long[] eventIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, (AuditMetadata? After, AuditMetadata? Previous)>();
        var states = new List<(long StateId, long EventId, string Side, AuditMetadata State)>();

        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync(cancellationToken);
        var idList = string.Join(",", eventIds);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT id, event_id, side, content, status, memory_source FROM audit_state WHERE event_id IN ({idList})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                states.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    new AuditMetadata
                    {
                        Content = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Status = reader.IsDBNull(4) ? null : reader.GetString(4),
                        MemorySource = reader.IsDBNull(5) ? null : reader.GetString(5)
                    }));
            }
        }

        var tags = new Dictionary<long, List<string>>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT state_id, tag FROM audit_tags WHERE state_id IN (SELECT id FROM audit_state WHERE event_id IN ({idList})) ORDER BY state_id, ordinal";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var stateId = reader.GetInt64(0);
                if (!tags.TryGetValue(stateId, out var list))
                {
                    list = [];
                    tags[stateId] = list;
                }
                list.Add(reader.GetString(1));
            }
        }

        foreach (var (stateId, eventId, side, state) in states)
        {
            var complete = tags.TryGetValue(stateId, out var list)
                ? new AuditMetadata { Content = state.Content, Status = state.Status, MemorySource = state.MemorySource, Tags = list }
                : state;
            var pair = result.TryGetValue(eventId, out var existing) ? existing : (null, null);
            result[eventId] = side == After ? (complete, pair.Previous) : (pair.After, complete);
        }

        return result;
    }

    private static AuditEvent WithState(AuditEvent e, AuditMetadata? after, AuditMetadata? previous) => new()
    {
        SchemaVersion = e.SchemaVersion,
        Timestamp = e.Timestamp,
        Trail = e.Trail,
        Category = e.Category,
        Action = e.Action,
        Actor = e.Actor,
        Source = e.Source,
        Scope = e.Scope,
        ProjectRoot = e.ProjectRoot,
        Target = e.Target,
        Outcome = e.Outcome,
        DurationMs = e.DurationMs,
        ByteSize = e.ByteSize,
        Hash = e.Hash,
        PrevHash = e.PrevHash,
        Query = e.Query,
        ResultCount = e.ResultCount,
        Path = e.Path,
        Matches = e.Matches,
        Pid = e.Pid,
        Metadata = after,
        PreviousMetadata = previous
    };

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~SqliteAuditIndexTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Audit tests/Eling.Core.Tests/SqliteAuditIndexTests.cs
git commit -m "feat(audit): add rebuildable sqlite audit index"
```

---

### Task 7: Buffered audit logger (channel + batch flush)

**Files:**
- Create: `src/backend/Eling.Core/Audit/Storage/BufferedAuditLogger.cs`
- Test: `tests/Eling.Core.Tests/BufferedAuditLoggerTests.cs`

**Interfaces:**
- Consumes: `IAuditLogger` (Task 3), `AuditOptions` (Task 3), `AuditFileStore` (Tasks 4–5), `AuditRotation` (Task 5), `IAuditIndex`/`AuditRecord` (Task 6).
- Produces: `sealed class BufferedAuditLogger : IAuditLogger, IAsyncDisposable` constructed as
  `BufferedAuditLogger(AuditFileStore store, IAuditIndex? index, AuditOptions options, TimeProvider timeProvider)`.
  - Fills `Timestamp`, `Actor`, `Source` from the ambient context when unset.
  - Change trail: `RecordAsync` completes after the entry is enqueued **and** the flusher has attempted the write within `FlushTimeout`.
  - Access trail: fire-and-forget.
  - `DisposeAsync` drains the channel and flushes.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using Eling.Core.Audit;
using Eling.Core.Audit.Storage;
using Xunit;

namespace Eling.Core.Tests;

public class BufferedAuditLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eling-audit-buf-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RecordAsync_ChangeTrail_IsDurableBeforeReturning()
    {
        var store = new AuditFileStore(_root, TimeProvider.System);
        await using var logger = new BufferedAuditLogger(store, index: null, new AuditOptions(), TimeProvider.System);

        using (AuditActorContext.Begin(new AuditActor("mcp:eling_dev", "mcp_stdio")))
        {
            await logger.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Change,
                Category = AuditCategory.Memory,
                Action = "memory_save",
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        var path = store.GetCurrentFilePath(AuditTrail.Change, DateTimeOffset.UtcNow);
        var line = (await File.ReadAllLinesAsync(path)).Single();
        var entry = JsonSerializer.Deserialize(line, AuditJsonContext.Default.AuditEvent)!;
        Assert.Equal("mcp:eling_dev", entry.Actor);
        Assert.Equal("mcp_stdio", entry.Source);
    }

    [Fact]
    public async Task DisposeAsync_DrainsPendingAccessEntries()
    {
        var store = new AuditFileStore(_root, TimeProvider.System);
        var logger = new BufferedAuditLogger(store, index: null, new AuditOptions(), TimeProvider.System);

        await logger.RecordAsync(new AuditEvent
        {
            Trail = AuditTrail.Access,
            Category = AuditCategory.Memory,
            Action = "memory_read",
            Timestamp = DateTimeOffset.UtcNow
        });

        await logger.DisposeAsync();

        var path = store.GetCurrentFilePath(AuditTrail.Access, DateTimeOffset.UtcNow);
        Assert.True(File.Exists(path));
        Assert.Single(await File.ReadAllLinesAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~BufferedAuditLoggerTests`
Expected: FAIL — `BufferedAuditLogger` does not exist.

- [ ] **Step 3: Write the minimal implementation**

```csharp
using System.Threading.Channels;

namespace Eling.Core.Audit.Storage;

/// <summary>
/// Single-writer logger. Entries enter a bounded channel; one background loop
/// drains and appends them in batches under a semaphore, so hot read paths
/// never take the file lock and appends are not one-fsync-per-entry. Change
/// entries wait (bounded) for the write; access entries are fire-and-forget.
/// </summary>
public sealed class BufferedAuditLogger : IAuditLogger, IAsyncDisposable
{
    private readonly AuditFileStore _store;
    private readonly IAuditIndex? _index;
    private readonly AuditOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<AuditEvent> _channel;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    public BufferedAuditLogger(AuditFileStore store, IAuditIndex? index, AuditOptions options, TimeProvider timeProvider)
    {
        _store = store;
        _index = index;
        _options = options;
        _timeProvider = timeProvider;
        _channel = Channel.CreateBounded<AuditEvent>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        _pump = Task.Run(PumpAsync);
    }

    public async ValueTask RecordAsync(AuditEvent entry, CancellationToken cancellationToken = default)
    {
        var actor = AuditActorContext.Current;
        var enriched = new AuditEvent
        {
            SchemaVersion = entry.SchemaVersion,
            Timestamp = entry.Timestamp == default ? _timeProvider.GetUtcNow() : entry.Timestamp,
            Trail = entry.Trail,
            Category = entry.Category,
            Action = entry.Action,
            Actor = entry.Actor == AuditActor.Unknown.Actor ? actor.Actor : entry.Actor,
            Source = entry.Source == AuditActor.Unknown.Source ? actor.Source : entry.Source,
            Scope = entry.Scope,
            ProjectRoot = entry.ProjectRoot,
            Target = entry.Target,
            Outcome = entry.Outcome,
            DurationMs = entry.DurationMs,
            ByteSize = entry.ByteSize,
            Hash = entry.Hash,
            PrevHash = entry.PrevHash,
            Metadata = entry.Metadata
        };

        _channel.Writer.TryWrite(enriched);

        if (entry.Trail == AuditTrail.Change)
        {
            // Bounded wait so a stuck writer cannot freeze the caller; fail-open.
            await Task.WhenAny(_pump, Task.Delay(_options.FlushTimeout, cancellationToken));
        }
    }

    private async Task PumpAsync()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync(_cts.Token))
        {
            var batch = new List<AuditEvent>(_options.FlushBatchSize);
            while (batch.Count < _options.FlushBatchSize && reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            foreach (var group in batch.GroupBy(e => e.Trail))
            {
                await FlushAsync(group.Key, group.ToList());
            }
        }
    }

    private async Task FlushAsync(AuditTrail trail, IReadOnlyList<AuditEvent> batch)
    {
        await _writeLock.WaitAsync(_cts.Token);
        try
        {
            var now = _timeProvider.GetUtcNow();
            await AuditRotation.RotateIfNeededAsync(_store, trail, now, _options.CompressOnRotate, _cts.Token);
            await _store.AppendAsync(trail, batch, flushToDisk: trail == AuditTrail.Change, _cts.Token);

            if (_index is not null)
            {
                var file = Path.GetFileName(_store.GetCurrentFilePath(trail, now));
                await _index.IndexAsync(
                    batch.Select(e => new AuditRecord(file, 0, e)).ToList(),
                    _cts.Token);
            }
        }
        catch
        {
            // Fail-open: an audit failure must never break the caller's operation.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best effort drain.
        }
        _cts.Cancel();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~BufferedAuditLoggerTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Audit tests/Eling.Core.Tests/BufferedAuditLoggerTests.cs
git commit -m "feat(audit): add buffered async audit logger"
```

---

### Task 8: Retention and daily rollups

**Files:**
- Create: `src/backend/Eling.Core/Audit/Retention/AuditRollup.cs`
- Create: `src/backend/Eling.Core/Audit/Retention/AuditRetentionPolicy.cs`
- Create: `src/backend/Eling.Core/Audit/Retention/AuditMaintenanceJob.cs`
- Test: `tests/Eling.Core.Tests/AuditRetentionTests.cs`

**Interfaces:**
- Consumes: `AuditFileStore` (Task 4), `AuditEvent` (Task 2), `AuditOptions` (Task 3), `AuditTrail` (Task 1).
- Produces:
  - `sealed record AuditRollup(DateTimeOffset Date, AuditTrail Trail, string Category, string Action, string Actor, string Outcome, int Count, int? MinMs, int? MedianMs, int? MaxMs)`
  - `static class AuditRetentionPolicy` with `bool IsExpired(AuditTrail trail, DateTimeOffset fileDate, DateTimeOffset now, AuditOptions options)`.
  - `sealed class AuditMaintenanceJob(AuditFileStore store, AuditOptions options, TimeProvider timeProvider)` with `Task RunAsync(CancellationToken)` — writes rollups for closed days, then deletes raw daily files past retention whose rollup exists.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using Eling.Core.Audit;
using Eling.Core.Audit.Retention;
using Eling.Core.Audit.Storage;
using Xunit;

namespace Eling.Core.Tests;

public class AuditRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eling-audit-ret-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void IsExpired_AccessExpiresBeforeChange()
    {
        var options = new AuditOptions { ChangeRetentionDays = 365, AccessRetentionDays = 14 };
        var fileDate = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

        Assert.False(AuditRetentionPolicy.IsExpired(AuditTrail.Change, fileDate, now, options));
        Assert.True(AuditRetentionPolicy.IsExpired(AuditTrail.Access, fileDate, now, options));
    }

    [Fact]
    public async Task RunAsync_WritesRollupAndDeletesExpiredRaw()
    {
        var store = new AuditFileStore(_root, TimeProvider.System);
        var old = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await store.AppendAsync(
            AuditTrail.Access,
            [new AuditEvent { Timestamp = old, Trail = AuditTrail.Access, Category = AuditCategory.Memory, Action = "memory_read", Actor = "mcp", Outcome = AuditOutcome.Success }],
            flushToDisk: true,
            CancellationToken.None);

        var options = new AuditOptions { AccessRetentionDays = 14 };
        var now = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
        var job = new AuditMaintenanceJob(store, options, new FixedTimeProvider(now));

        await job.RunAsync(CancellationToken.None);

        var rollupDir = Path.Combine(_root, "rollups");
        var rollupFile = Directory.GetFiles(rollupDir, "*.jsonl").Single();
        var rollup = JsonSerializer.Deserialize(File.ReadAllLines(rollupFile).Single(), AuditJsonContext.Default.AuditRollup)!;
        Assert.Equal("memory_read", rollup.Action);
        Assert.Equal(1, rollup.Count);
        Assert.False(File.Exists(Path.Combine(_root, "access", "2026-08-01.jsonl")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~AuditRetentionTests`
Expected: FAIL — `AuditMaintenanceJob` / `AuditRollup` / `AuditRetentionPolicy` do not exist.

- [ ] **Step 3: Write the minimal implementation**

`AuditRollup.cs`:
```csharp
namespace Eling.Core.Audit.Retention;

public sealed record AuditRollup(
    DateTimeOffset Date,
    AuditTrail Trail,
    string Category,
    string Action,
    string Actor,
    string Outcome,
    int Count,
    int? MinMs,
    int? MedianMs,
    int? MaxMs);
```

Add to `AuditJsonContext.cs` a second serializable type (append `[JsonSerializable(typeof(AuditRollup))]` and the `using Eling.Core.Audit.Retention;`). This is the one permitted cross-namespace touch in Phase 1.

`AuditRetentionPolicy.cs`:
```csharp
namespace Eling.Core.Audit.Retention;

public static class AuditRetentionPolicy
{
    public static bool IsExpired(AuditTrail trail, DateTimeOffset fileDate, DateTimeOffset now, AuditOptions options)
    {
        var days = trail == AuditTrail.Change ? options.ChangeRetentionDays : options.AccessRetentionDays;
        return now - fileDate > TimeSpan.FromDays(days);
    }
}
```

`AuditMaintenanceJob.cs`:
```csharp
using System.Text.Json;
using Eling.Core.Audit.Storage;

namespace Eling.Core.Audit.Retention;

public sealed class AuditMaintenanceJob(AuditFileStore store, AuditOptions options, TimeProvider timeProvider)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var rollupDir = Path.Combine(store.GetCurrentFilePath(AuditTrail.Change, now), "..", "..", "rollups");
        rollupDir = Path.GetFullPath(rollupDir);
        Directory.CreateDirectory(rollupDir);

        foreach (var trail in new[] { AuditTrail.Change, AuditTrail.Access })
        {
            var dir = Path.Combine(Path.GetDirectoryName(store.GetCurrentFilePath(trail, now))!);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
            {
                var date = DateTimeOffset.Parse(Path.GetFileNameWithoutExtension(file));
                if (!AuditRetentionPolicy.IsExpired(trail, date, now, options)) continue;

                var entries = (await File.ReadAllLinesAsync(file, cancellationToken))
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => JsonSerializer.Deserialize(l, AuditJsonContext.Default.AuditEvent)!)
                    .ToList();

                foreach (var group in entries.GroupBy(e => new { e.Category, e.Action, e.Actor, e.Outcome }))
                {
                    var durations = group.Select(e => e.DurationMs).Where(d => d.HasValue).Select(d => d!.Value).OrderBy(d => d).ToList();
                    var rollup = new AuditRollup(
                        date, trail, group.Key.Category.ToString(), group.Key.Action, group.Key.Actor, group.Key.Outcome,
                        group.Count(),
                        durations.Count > 0 ? durations[0] : null,
                        durations.Count > 0 ? durations[durations.Count / 2] : null,
                        durations.Count > 0 ? durations[^1] : null);
                    await File.AppendAllTextAsync(
                        Path.Combine(rollupDir, $"{date:yyyy-MM-dd}.jsonl"),
                        JsonSerializer.Serialize(rollup, AuditJsonContext.Default.AuditRollup) + "\n",
                        cancellationToken);
                }

                File.Delete(file);
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter FullyQualifiedName~AuditRetentionTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Audit tests/Eling.Core.Tests/AuditRetentionTests.cs
git commit -m "feat(audit): add retention policy and daily rollups"
```

---

## Self-Review

**1. Spec coverage (Phase 1 slice):** §4 schema → Tasks 1–2. §5 actor → Tasks 1, 7. §6.1 layout/§6.2 rotation/§6.5 repo hygiene → Tasks 0, 4–5. §6.3 index → Task 6. §6.4 rollups/§13 retention → Task 8. §7.1 in-process / §7.2 cross-process / §7.3 durability → Tasks 4, 7. §14 fail-open → Task 7 (`FlushAsync` catch). Hooks (§8), read API (§9), realtime (§10), UI (§11), exclusions (§12) are intentionally out of Phase 1 and tracked in Phases 2–4.

**2. Placeholder scan:** No "TBD"/"handle edge cases"/"similar to Task N". Every code step carries real code.

**3. Type consistency:** `AuditFileStore.GetCurrentFilePath` is used identically in Tasks 5–8. `AuditEvent` property names match between Tasks 2, 7, and 8. `AuditRecord`/`AuditQuery` are defined once (Task 6) and consumed in Task 7. `AuditTrail.Access` uses the `access/` directory consistently in Tasks 4, 6, and 8.

**Known follow-ups for later phases (do not implement now):**
- `SqliteAuditIndex.IndexAsync` is called with an interim `line: 0` from the logger; a later phase can compute exact line offsets or drop the column's precision requirement.
- `BufferedAuditLogger` registers `_pump` before fields are fully initialized in the constructor; if a future editor reorders initialization, guard against the captured `this` by starting the pump in an `InitializeAsync` if needed.
