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
