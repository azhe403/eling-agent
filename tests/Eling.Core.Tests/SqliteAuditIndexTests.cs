using Eling.Core.Audit;
using Eling.Core.Audit.Storage;
using Eling.Core.Memory;
using Microsoft.Data.Sqlite;
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
    public async Task RebuildAsync_SeesCompressedClosedDays()
    {
        var day = new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);
        var store = new AuditFileStore(_root, new FixedTimeProvider(day));
        await store.AppendAsync(
            AuditTrail.Change,
            [new AuditEvent { Timestamp = day, Trail = AuditTrail.Change, Action = "memory_save" }],
            flushToDisk: true,
            CancellationToken.None);

        // Production compresses every closed day. A rebuild after rotation must
        // still see it: Directory.GetFiles(dir, "*.jsonl") does not match
        // "*.jsonl.gz", so the rebuild silently dropped every closed day from an
        // index whose contract is "rescans every JSONL file".
        await AuditRotation.RotateIfNeededAsync(store, AuditTrail.Change, day.AddDays(1), compress: true, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_root, "changes", "2026-09-10.jsonl.gz")));

        var index = new SqliteAuditIndex(_root, store);
        await index.RebuildAsync(CancellationToken.None);

        var entry = (await index.QueryAsync(new AuditQuery(AuditTrail.Change, null, null), CancellationToken.None)).Single().Event;
        Assert.Equal("memory_save", entry.Action);
        Assert.Equal(day, entry.Timestamp);
    }

    [Fact]
    public async Task IndexAsync_StoresTheCanonicalKebabLabels()
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
                Actor = "mcp",
                Source = "mcp_stdio",
                Scope = MemoryScopeKind.ProjectLocal,
                Outcome = AuditOutcome.Error
            })
        ], CancellationToken.None);

        // The index and the JSONL speak one vocabulary (amendment C1). The
        // PascalCase rows this used to store were a second vocabulary, and
        // routing the write sites through the kebab labels is only sound because
        // the read sites accept them too — Enum.Parse("project-local") would
        // throw, so the scope is read back through the scope parser.
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "audit-index.db")};Pooling=False");
        await connection.OpenAsync(CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT trail, category, outcome, scope FROM audit_events;";
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("change", reader.GetString(0));
            Assert.Equal("memory", reader.GetString(1));
            Assert.Equal("error", reader.GetString(2));
            Assert.Equal("project-local", reader.GetString(3));
        }

        var row = (await index.QueryAsync(new AuditQuery(AuditTrail.Change, null, null), CancellationToken.None)).Single();
        Assert.Equal(MemoryScopeKind.ProjectLocal, row.Event.Scope);
        Assert.Equal(AuditOutcome.Error, row.Event.Outcome);
    }

    [Fact]
    public async Task QueryAsync_ReadsRowsWrittenByThePreviousLabelVocabulary()
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
                Actor = "mcp",
                Scope = MemoryScopeKind.ProjectLocal,
                Outcome = AuditOutcome.Error
            })
        ], CancellationToken.None);

        // An upgraded database still holds rows written by the previous
        // PascalCase writer. The index is a rebuildable cache, but a query that
        // returns nothing — and a scope that reads back null — is a silent
        // misread, not a cache miss: SQLite's TEXT "=" is case-sensitive, and
        // MemoryScopeParser lowercases "ProjectLocal" to "projectlocal", which
        // matches no scope at all.
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "audit-index.db")};Pooling=False");
        await connection.OpenAsync(CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE audit_events SET trail = 'Change', category = 'Memory', outcome = 'Error', scope = 'ProjectLocal' WHERE id = (SELECT MAX(id) FROM audit_events);";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var row = (await index.QueryAsync(new AuditQuery(AuditTrail.Change, null, null), CancellationToken.None)).Single();
        Assert.Equal(AuditTrail.Change, row.Event.Trail);
        Assert.Equal(AuditCategory.Memory, row.Event.Category);
        Assert.Equal(AuditOutcome.Error, row.Event.Outcome);
        Assert.Equal(MemoryScopeKind.ProjectLocal, row.Event.Scope);
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

    internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
