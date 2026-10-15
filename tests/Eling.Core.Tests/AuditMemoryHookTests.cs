using System.Text.Json;
using Eling.Core.Audit;
using Eling.Core.Audit.Storage;
using Eling.Core.Memory;
using Eling.Core.Memory.Storage;
using Eling.Core.Scope;
using Xunit;

namespace Eling.Core.Tests;

/// <summary>
/// The memory domain's audit hooks (design §8): a save, an update, a delete and
/// a copy each record exactly one change entry with the before/after snapshots
/// §4.2 pins, while a get and a search record access entries carrying the query
/// and the count — never the contents. Hermetic on purpose: a temp audit root,
/// an in-memory memory store and a fixed clock, so the entry file, its
/// timestamps and the labels are all deterministic.
/// </summary>
[Collection(AuditTrailCollection.Name)]
public sealed class AuditMemoryHookTests : IAsyncLifetime
{
    private const string ProjectRoot = @"C:\work\acme";
    private const string CanonicalRoot = @"C:\work\acme";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eling-audit-hook-" + Guid.NewGuid().ToString("N"));

    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
    private readonly AuditFileStore _store;
    private readonly SqliteAuditIndex _index;
    private readonly BufferedAuditLogger _logger;
    private bool _drained;

    public AuditMemoryHookTests()
    {
        _store = new AuditFileStore(_root, _time);
        _index = new SqliteAuditIndex(_root, _store);
        _logger = new BufferedAuditLogger(
            _store,
            _index,
            // A short accumulation window: the access trail is fire-and-forget,
            // so its entry lands whenever the pump next batches. The design's
            // 250ms default only slows the test down.
            new AuditOptions { FlushInterval = TimeSpan.FromMilliseconds(10) },
            _time);
    }

    public Task InitializeAsync() => _index.InitializeAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        // DisposeAsync is not idempotent on the engine's logger, and a test that
        // drains it early (see DrainAndReadAccessEntriesAsync) must not dispose
        // it a second time here.
        if (!_drained)
        {
            await _logger.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Save_Created_RecordsOneChangeEntry_WithActorScopeLabelAndSnapshot()
    {
        var service = Build();
        using var actor = AuditActorContext.Begin(new AuditActor("mcp:eling_dev", "mcp_stdio"));

        var saved = await service.SaveAsync(
            new Memory.Memory(MemoryType.Fact, "audit hook remembers the save", ["tagone", "tagtwo"], "user_chat"));

        var entry = Assert.Single(await ChangeEntriesAsync());
        var line = Assert.Single(await ChangeLinesAsync());

        Assert.Equal("memory_save", entry.Action);
        Assert.Equal(AuditTrail.Change, entry.Trail);
        Assert.Equal(AuditCategory.Memory, entry.Category);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        // The actor comes from the ambient scope the MCP filter opens, not from
        // the call site (design §5).
        Assert.Equal("mcp:eling_dev", entry.Actor);
        Assert.Equal("mcp_stdio", entry.Source);
        // The 2026-10-14 default: a blank scope routes to the project-local shard,
        // whose project label is the folder name of the canonical root.
        Assert.Equal(MemoryScopeKind.ProjectLocal, entry.Scope);
        Assert.Equal("acme", entry.ProjectRoot);
        Assert.Equal(saved.Memory.Id.Value, entry.Target);
        Assert.Equal(_time.GetUtcNow(), entry.Timestamp);

        // The after-state: content, tags, status, memory source (§4.2) — and no
        // before-state for a create.
        Assert.Equal("audit hook remembers the save", entry.Metadata!.Content);
        Assert.Equal(new[] { "tagone", "tagtwo" }, entry.Metadata.Tags);
        Assert.Equal("active", entry.Metadata.Status);
        Assert.Equal("user_chat", entry.Metadata.MemorySource);
        Assert.Null(entry.PreviousMetadata);

        // No machine-specific absolute path anywhere in the entry (§4.1, A1): a
        // backslash anywhere in the line would mean a path leaked through.
        Assert.DoesNotContain(ProjectRoot, line, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\\", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_SmartSaveMerge_RecordsBeforeAndAfterState()
    {
        var service = Build();

        var created = await service.SaveAsync(
            new Memory.Memory(MemoryType.Fact, "audit hook merged content", ["before"]));
        var updated = await service.SaveAsync(
            new Memory.Memory(MemoryType.Fact, "audit hook merged content", ["after"]));

        Assert.Equal(SaveAction.Updated, updated.Action);
        var entries = await ChangeEntriesAsync();

        // Exactly two entries: one for the create, one for the merge.
        Assert.Equal(2, entries.Count);
        var first = entries[0];
        var second = entries[1];

        Assert.Equal("memory_save", first.Action);
        Assert.Equal(created.Memory.Id.Value, first.Target);
        Assert.Null(first.PreviousMetadata);
        // No ambient scope here, so the entry falls back to the logger's default.
        Assert.Equal("unknown", first.Actor);

        // The merge entry carries both sides of the same memory: the before tags
        // it merged away from and the after tags it merged into (§4.2).
        Assert.Equal("memory_save", second.Action);
        Assert.Equal(updated.Memory.Id.Value, second.Target);
        Assert.Equal(created.Memory.Id.Value, updated.Memory.Id.Value);
        Assert.Equal(["before"], second.PreviousMetadata!.Tags);
        Assert.Equal("audit hook merged content", second.PreviousMetadata.Content);
        Assert.Equal(["before", "after"], second.Metadata!.Tags);
        Assert.Equal("audit hook merged content", second.Metadata.Content);
    }

    [Fact]
    public async Task Update_RecordsBeforeAndAfterState()
    {
        // The explicit update path reads the before-state first, so the entry
        // carries both sides even though the memory service patches in place.
        var service = Build();
        var saved = await service.SaveAsync(
            new Memory.Memory(MemoryType.Fact, "audit hook before update", ["old"]));

        var updated = await service.UpdateAsync(
            MemoryReference.ForProjectLocal(saved.Memory.Id, CanonicalRoot),
            content: "audit hook after update",
            tags: ["new"]);

        Assert.NotNull(updated);
        var entry = Assert.Single(
            await ChangeEntriesAsync(),
            e => e.PreviousMetadata is not null);

        Assert.Equal("memory_save", entry.Action);
        Assert.Equal(saved.Memory.Id.Value, entry.Target);
        Assert.Equal("audit hook before update", entry.PreviousMetadata!.Content);
        Assert.Equal(["old"], entry.PreviousMetadata.Tags);
        Assert.Equal("audit hook after update", entry.Metadata!.Content);
        Assert.Equal(["new"], entry.Metadata.Tags);
    }

    [Fact]
    public async Task Delete_RecordsEntryWithRemovedStateAsBeforeSide()
    {
        var service = Build();
        var saved = await service.SaveAsync(
            new Memory.Memory(MemoryType.Fact, "audit hook doomed", ["gone"], "from_chat"));

        var deleted = await service.DeleteAsync(
            MemoryReference.ForProjectLocal(saved.Memory.Id, CanonicalRoot));

        Assert.True(deleted);
        // Two change entries in total — the setup save and the delete — of which
        // exactly one is the delete.
        var entry = Assert.Single(await ChangeEntriesAsync(), e => e.Action == "memory_delete");

        Assert.Equal("memory_delete", entry.Action);
        Assert.Equal(saved.Memory.Id.Value, entry.Target);
        Assert.Equal(MemoryScopeKind.ProjectLocal, entry.Scope);
        Assert.Equal("acme", entry.ProjectRoot);
        // A delete has no after-state; the removed memory is the before-side.
        Assert.Null(entry.Metadata);
        Assert.Equal("audit hook doomed", entry.PreviousMetadata!.Content);
        Assert.Equal(["gone"], entry.PreviousMetadata.Tags);
        Assert.Equal("from_chat", entry.PreviousMetadata.MemorySource);
    }

    [Fact]
    public async Task Promote_RecordsTargetStateWithSourceStateAsBeforeSide()
    {
        var service = Build();
        var saved = await service.SaveAsync(
            new Memory.Memory(MemoryType.Fact, "audit hook promotable", ["tier"], "chat"));

        var promoted = await service.CopyToGlobalAsync(
            MemoryReference.ForProjectLocal(saved.Memory.Id, CanonicalRoot));

        Assert.NotNull(promoted);
        var entry = Assert.Single(
            await ChangeEntriesAsync(),
            e => e.Action == "memory_promote_to_global");

        Assert.Equal("memory_promote_to_global", entry.Action);
        Assert.Equal(promoted!.Memory.Id.Value, entry.Target);
        Assert.Equal(MemoryScopeKind.Global, entry.Scope);
        Assert.Null(entry.ProjectRoot);
        Assert.Equal("audit hook promotable", entry.Metadata!.Content);
        Assert.Equal(["tier"], entry.Metadata.Tags);
        Assert.Equal("audit hook promotable", entry.PreviousMetadata!.Content);
    }

    [Fact]
    public async Task Search_RecordsAccessEntryWithQueryAndCount_AndNoContent()
    {
        var service = Build();
        await service.SaveAsync(new Memory.Memory(MemoryType.Fact, "audit hook searchable body", ["find"]));

        var results = await service.SearchAsync("audit hook", "project-local");

        Assert.Single(results);
        var entry = Assert.Single(await DrainAndReadAccessEntriesAsync());

        Assert.Equal("memory_search", entry.Action);
        Assert.Equal(AuditTrail.Access, entry.Trail);
        Assert.Equal(AuditCategory.Memory, entry.Category);
        Assert.Equal("audit hook", entry.Query);
        Assert.Equal(1, entry.ResultCount);
        Assert.Null(entry.Metadata);
        Assert.Null(entry.PreviousMetadata);

        var line = Assert.Single(await AccessLinesAsync());
        // The access trail records event detail only: the matched memory's
        // content never reaches disk (design §4).
        Assert.DoesNotContain("audit hook searchable body", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_RecordsAccessEntryWithTargetAndCount_AndNoContent()
    {
        var service = Build();
        var saved = await service.SaveAsync(
            new Memory.Memory(MemoryType.Fact, "audit hook fetched", ["read"]));

        var found = await service.GetByIdAsync(
            MemoryReference.ForProjectLocal(saved.Memory.Id, CanonicalRoot));

        Assert.NotNull(found);
        var entry = Assert.Single(await DrainAndReadAccessEntriesAsync());

        Assert.Equal("memory_get", entry.Action);
        Assert.Equal(AuditTrail.Access, entry.Trail);
        Assert.Equal(saved.Memory.Id.Value, entry.Target);
        Assert.Equal(MemoryScopeKind.ProjectLocal, entry.Scope);
        Assert.Equal("acme", entry.ProjectRoot);
        // One memory read: one result when it was found.
        Assert.Equal(1, entry.ResultCount);
        Assert.Null(entry.Query);
        Assert.Null(entry.Metadata);

        var line = Assert.Single(await AccessLinesAsync());
        Assert.DoesNotContain("audit hook fetched", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_RecordsAccessEntryWithCountOnly()
    {
        var service = Build();
        await service.SaveAsync(new Memory.Memory(MemoryType.Fact, "audit hook listed", ["list"]));

        var memories = await service.ListAsync("project-local");

        Assert.Single(memories);
        var entry = Assert.Single(await DrainAndReadAccessEntriesAsync());

        Assert.Equal("memory_list", entry.Action);
        Assert.Equal(1, entry.ResultCount);
        Assert.Null(entry.Query);
        Assert.Null(entry.Target);
        Assert.Null(entry.Metadata);
    }

    [Fact]
    public async Task RecordedEntry_IsQueryableThroughTheSqliteIndex()
    {
        // The index is the read path's cache over the JSONL; a recorded entry is
        // only useful once both agree (design §6.3).
        var service = Build();
        using var actor = AuditActorContext.Begin(new AuditActor("mcp:eling", "mcp_stdio"));
        await service.SaveAsync(new Memory.Memory(MemoryType.Fact, "audit hook indexed", ["cache"]));

        var rows = await _index.QueryAsync(
            new AuditQuery(AuditTrail.Change, _time.GetUtcNow().AddMinutes(-1), _time.GetUtcNow().AddMinutes(1)),
            CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("memory_save", row.Event.Action);
        Assert.Equal("mcp:eling", row.Event.Actor);
        Assert.Equal(MemoryScopeKind.ProjectLocal, row.Event.Scope);
        Assert.Equal("acme", row.Event.ProjectRoot);
        Assert.Equal("audit hook indexed", row.Event.Metadata!.Content);
        Assert.Equal(["cache"], row.Event.Metadata.Tags);
    }

    /// <summary>
    /// One memory service per tier, all in memory, over the store under test:
    /// the real <see cref="MemoryService"/> smart-save merge is what produces
    /// the update entries, so no fake stands in for it.
    /// </summary>
    private ScopedMemoryService Build()
    {
        var project = new MemoryService(new InMemoryMemoryStorage(), new InMemoryMemoryIndex());
        var local = new MemoryService(new InMemoryMemoryStorage(), new InMemoryMemoryIndex());
        var global = new MemoryService(new InMemoryMemoryStorage(), new InMemoryMemoryIndex());
        var levels = new List<ProjectLevel> { new(new ProjectScope(ProjectRoot), project) };

        return new ScopedMemoryService(
            levels,
            global,
            new MemoryScopePolicy(),
            new MemoryMerger(),
            ProjectRoot,
            localService: local,
            canonicalRoot: CanonicalRoot,
            auditLogger: _logger);
    }

    private string ChangeFilePath => _store.GetCurrentFilePath(AuditTrail.Change, _time.GetUtcNow());

    private string AccessFilePath => _store.GetCurrentFilePath(AuditTrail.Access, _time.GetUtcNow());

    private async Task<IReadOnlyList<string>> ChangeLinesAsync() =>
        File.Exists(ChangeFilePath) ? await File.ReadAllLinesAsync(ChangeFilePath) : [];

    private async Task<IReadOnlyList<string>> AccessLinesAsync() =>
        File.Exists(AccessFilePath) ? await File.ReadAllLinesAsync(AccessFilePath) : [];

    private async Task<IReadOnlyList<AuditEvent>> ChangeEntriesAsync() => await ReadAsync(ChangeLinesAsync());

    /// <summary>
    /// The access trail is fire-and-forget (design §7.3): nothing in the caller's
    /// return says its entry landed. Disposing the logger drains the channel —
    /// the same shutdown drain the host relies on — which makes this read
    /// deterministic instead of a race against the pump.
    /// </summary>
    private async Task<IReadOnlyList<AuditEvent>> DrainAndReadAccessEntriesAsync()
    {
        _drained = true;
        await _logger.DisposeAsync();
        return await ReadAsync(AccessLinesAsync());
    }

    private static async Task<IReadOnlyList<AuditEvent>> ReadAsync(Task<IReadOnlyList<string>> lines)
    {
        var collected = await lines;
        var entries = new List<AuditEvent>(collected.Count);
        foreach (var line in collected)
        {
            entries.Add(JsonSerializer.Deserialize(line, AuditJsonContext.Default.AuditEvent)!);
        }

        return entries;
    }

    // Nested rather than a second top-level type: a file-scoped helper would
    // break CodeOrganizationConventionTests' one-type-per-file rule, which this
    // repo tracks.
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
