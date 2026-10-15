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
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        var store = new AuditFileStore(_root, new FixedTimeProvider(now));
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

    // Nested rather than a second top-level type: a file-scoped helper would
    // break CodeOrganizationConventionTests' one-type-per-file rule, which
    // this repo tracks.
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
