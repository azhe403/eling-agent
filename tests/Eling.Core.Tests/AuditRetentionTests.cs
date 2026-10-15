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
        var old = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var store = new AuditFileStore(_root, new FixedTimeProvider(old));
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

    internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
