using System.Text.Json;
using Eling.Core.Audit;
using Eling.Core.Audit.Storage;
using Eling.Core.Memory;
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
                Action = "memory_update",
                Timestamp = DateTimeOffset.UtcNow,
                Scope = MemoryScopeKind.ProjectLocal,
                ProjectRoot = "eling-agent",
                Target = "01m0000000000000000000000",
                Outcome = AuditOutcome.Success,
                DurationMs = 12,
                ByteSize = 256,
                Hash = "sha256:abc",
                PrevHash = "sha256:def",
                // Access-only detail and the change-trail snapshots: a change
                // entry carrying every field is what pins the enrichment copy.
                Query = "eling build steps",
                ResultCount = 7,
                Path = "src/a.cs",
                Matches = 3,
                Pid = 4242,
                Metadata = new AuditMetadata { Content = "after", Tags = ["audit"], Status = "active", MemorySource = "user_chat" },
                PreviousMetadata = new AuditMetadata { Content = "before", Tags = ["old"], Status = "active" }
            });
        }

        var path = store.GetCurrentFilePath(AuditTrail.Change, DateTimeOffset.UtcNow);
        var line = (await File.ReadAllLinesAsync(path)).Single();
        var entry = JsonSerializer.Deserialize(line, AuditJsonContext.Default.AuditEvent)!;

        // Every field of the enriched copy round-trips. The copy used to drop
        // Action, the whole access-detail block and the before-snapshot, so the
        // store silently diverged from the index that kept persisting them.
        Assert.Equal(1, entry.SchemaVersion);
        Assert.Equal(AuditTrail.Change, entry.Trail);
        Assert.Equal(AuditCategory.Memory, entry.Category);
        Assert.Equal("memory_update", entry.Action);
        Assert.Equal("mcp:eling_dev", entry.Actor);
        Assert.Equal("mcp_stdio", entry.Source);
        Assert.Equal(MemoryScopeKind.ProjectLocal, entry.Scope);
        Assert.Equal("eling-agent", entry.ProjectRoot);
        Assert.Equal("01m0000000000000000000000", entry.Target);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Equal(12, entry.DurationMs);
        Assert.Equal(256, entry.ByteSize);
        Assert.Equal("sha256:abc", entry.Hash);
        Assert.Equal("sha256:def", entry.PrevHash);
        Assert.Equal("eling build steps", entry.Query);
        Assert.Equal(7, entry.ResultCount);
        Assert.Equal("src/a.cs", entry.Path);
        Assert.Equal(3, entry.Matches);
        Assert.Equal(4242, entry.Pid);
        Assert.Equal("after", entry.Metadata!.Content);
        Assert.Equal(new[] { "audit" }, entry.Metadata.Tags);
        Assert.Equal("active", entry.Metadata.Status);
        Assert.Equal("user_chat", entry.Metadata.MemorySource);
        Assert.Equal("before", entry.PreviousMetadata!.Content);
        Assert.Equal(new[] { "old" }, entry.PreviousMetadata.Tags);
        Assert.Equal("active", entry.PreviousMetadata.Status);
        Assert.NotEqual(default, entry.Timestamp);
    }

    [Fact]
    public async Task RecordAsync_NormalizesCallerTimestampToUtc()
    {
        var store = new AuditFileStore(_root, TimeProvider.System);
        await using var logger = new BufferedAuditLogger(store, index: null, new AuditOptions(), TimeProvider.System);

        // A caller's local offset is advisory data, not a storage-format
        // decision: the logger owns the timestamp (design §4.1, amendment C2).
        var local = new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.FromHours(2));
        await logger.RecordAsync(new AuditEvent
        {
            Trail = AuditTrail.Change,
            Category = AuditCategory.Memory,
            Action = "memory_save",
            Timestamp = local
        });

        var path = store.GetCurrentFilePath(AuditTrail.Change, DateTimeOffset.UtcNow);
        var line = (await File.ReadAllLinesAsync(path)).Single();

        Assert.Contains("+00:00", line);
        Assert.DoesNotContain("+02:00", line);
        var entry = JsonSerializer.Deserialize(line, AuditJsonContext.Default.AuditEvent)!;
        Assert.Equal(TimeSpan.Zero, entry.Timestamp.Offset);
        Assert.Equal(local.UtcDateTime, entry.Timestamp.UtcDateTime);
    }

    [Fact]
    public async Task RecordAsync_WhenTheChannelBacksUp_NeverDropsAnEntry()
    {
        var store = new AuditFileStore(_root, TimeProvider.System);
        var index = new BlockingIndex();
        var options = new AuditOptions
        {
            FlushBatchSize = 64,
            FlushTimeout = TimeSpan.FromMilliseconds(300)
        };
        // Not `await using`: this test drives the drain through DisposeAsync
        // itself, and a second disposal runs during stack unwinding.
        var logger = new BufferedAuditLogger(store, index, options, TimeProvider.System);

        // Stall the pump: the first batch is appended, then the index blocks
        // inside the flush while the write lock is held, so nothing is drained
        // and the bounded 4096-slot channel fills behind it. Deterministic — no
        // sleeps, the pump announces that it is parked.
        var recorded = new List<Task> { logger.RecordAsync(ChangeEntry()).AsTask() };
        await index.Entered;

        const int total = 4096 + 32;
        for (var i = 1; i < total; i++)
        {
            recorded.Add(logger.RecordAsync(ChangeEntry()).AsTask());
        }

        // Every caller is bounded by FlushTimeout, and none of them is told the
        // entry reached disk while the pump is stalled.
        await Task.WhenAll(recorded);
        var path = store.GetCurrentFilePath(AuditTrail.Change, DateTimeOffset.UtcNow);
        Assert.True((await File.ReadAllLinesAsync(path)).Length < total);

        // Unblock: the queued entries must all land. Dropping them here is what
        // a DropWrite channel does — never written, their confirmation never
        // set, and every caller long since returned as if the write happened.
        index.Release();
        await logger.DisposeAsync();

        Assert.Equal(total, (await File.ReadAllLinesAsync(path)).Length);
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

    [Fact]
    public void AuditOptions_CarryTheDesignDefaultsAndGuardTheBatchSize()
    {
        var defaults = new AuditOptions();

        Assert.Equal(365, defaults.ChangeRetentionDays);
        Assert.Equal(14, defaults.AccessRetentionDays);
        Assert.Equal(128, defaults.FlushBatchSize);
        Assert.Equal(TimeSpan.FromMilliseconds(250), defaults.FlushInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), defaults.FlushTimeout);
        Assert.True(defaults.CompressOnRotate);
        Assert.True(defaults.CaptureQueryText);

        // A batch size of zero (or less) makes the pump's drain loop fill no
        // batch, so it spins through the channel without ever appending: every
        // change entry would wait out its FlushTimeout with nothing written.
        Assert.Throws<ArgumentOutOfRangeException>(() => new BufferedAuditLogger(
            new AuditFileStore(_root, TimeProvider.System),
            index: null,
            new AuditOptions { FlushBatchSize = 0 },
            TimeProvider.System));
    }

    private static AuditEvent ChangeEntry() => new()
    {
        Trail = AuditTrail.Change,
        Category = AuditCategory.Memory,
        Action = "memory_save",
        Timestamp = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// An index that parks the pump inside a flush until <see cref="Release"/>.
    /// The entries of that batch are already appended when it parks, so nothing
    /// is lost while the channel fills — which is exactly the state the
    /// backed-up-channel test needs.
    /// </summary>
    private sealed class BlockingIndex : IAuditIndex
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _released.TrySetResult();

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task IndexAsync(IReadOnlyList<AuditRecord> records, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _released.Task;
        }

        public Task RebuildAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AuditRecord>>([]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
