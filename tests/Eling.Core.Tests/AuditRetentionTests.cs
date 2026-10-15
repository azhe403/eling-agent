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

    [Fact]
    public async Task RunAsync_RollsUpCompressedDay()
    {
        var day = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var store = new AuditFileStore(_root, new FixedTimeProvider(day));
        await store.AppendAsync(
            AuditTrail.Access,
            [new AuditEvent { Timestamp = day, Trail = AuditTrail.Access, Category = AuditCategory.Memory, Action = "memory_read", Actor = "mcp", Outcome = AuditOutcome.Success }],
            flushToDisk: true,
            CancellationToken.None);

        // Production compresses every closed day (CompressOnRotate defaults true via
        // BufferedAuditLogger.FlushAsync -> AuditRotation); mirror it so only the
        // .jsonl.gz survives and the job must roll up the compressed form.
        await AuditRotation.RotateIfNeededAsync(store, AuditTrail.Access, day.AddDays(1), compress: true, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_root, "access", "2026-08-01.jsonl.gz")));
        Assert.False(File.Exists(Path.Combine(_root, "access", "2026-08-01.jsonl")));

        var options = new AuditOptions { AccessRetentionDays = 14 };
        var job = new AuditMaintenanceJob(store, options, new FixedTimeProvider(new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero)));

        await job.RunAsync(CancellationToken.None);

        var rollupFile = Directory.GetFiles(Path.Combine(_root, "rollups"), "*.jsonl").Single();
        var rollup = JsonSerializer.Deserialize(File.ReadAllLines(rollupFile).Single(), AuditJsonContext.Default.AuditRollup)!;
        Assert.Equal("memory_read", rollup.Action);
        Assert.Equal(1, rollup.Count);
        Assert.False(File.Exists(Path.Combine(_root, "access", "2026-08-01.jsonl.gz")));
    }

    [Fact]
    public async Task RunAsync_ReEncounteredDay_DoesNotDuplicateRollupLine()
    {
        var day = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var store = new AuditFileStore(_root, new FixedTimeProvider(day));
        await store.AppendAsync(
            AuditTrail.Access,
            [new AuditEvent { Timestamp = day, Trail = AuditTrail.Access, Category = AuditCategory.Memory, Action = "memory_read", Actor = "mcp", Outcome = AuditOutcome.Success }],
            flushToDisk: true,
            CancellationToken.None);

        // Capture the raw day's content so it can be re-placed, simulating a crash
        // between the rollup write and the raw delete: the next pass re-encounters
        // the very same day and must overwrite, not append, its group line.
        var rawPath = store.GetCurrentFilePath(AuditTrail.Access, day);
        var rawLines = await File.ReadAllLinesAsync(rawPath);

        var options = new AuditOptions { AccessRetentionDays = 14 };
        var job = new AuditMaintenanceJob(store, options, new FixedTimeProvider(new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero)));

        await job.RunAsync(CancellationToken.None);
        var rollupFile = Directory.GetFiles(Path.Combine(_root, "rollups"), "*.jsonl").Single();
        // First run folds the day into a single rollup line and deletes the raw file.
        Assert.Single(File.ReadAllLines(rollupFile));

        await File.WriteAllLinesAsync(rawPath, rawLines);
        await job.RunAsync(CancellationToken.None);

        // Overwrite, not append: still exactly one line, and the raw file is gone.
        Assert.Single(File.ReadAllLines(rollupFile));
        Assert.False(File.Exists(rawPath));
    }

    [Fact]
    public async Task RunAsync_MergesAccessAndChangeRollupsForSameDate()
    {
        // The rollup file for a date covers BOTH trails, but the trails expire on
        // different windows (access 14 days, change 365 by default), so their days
        // for one date are folded in different passes. The later change pass must
        // MERGE into the file the earlier access pass wrote, not clobber it.
        var day = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var store = new AuditFileStore(_root, new FixedTimeProvider(day));
        await store.AppendAsync(
            AuditTrail.Access,
            [new AuditEvent { Timestamp = day, Trail = AuditTrail.Access, Category = AuditCategory.Memory, Action = "memory_read", Actor = "mcp", Outcome = AuditOutcome.Success }],
            flushToDisk: true,
            CancellationToken.None);
        await store.AppendAsync(
            AuditTrail.Change,
            [new AuditEvent { Timestamp = day, Trail = AuditTrail.Change, Category = AuditCategory.Memory, Action = "memory_save", Actor = "mcp", Outcome = AuditOutcome.Success }],
            flushToDisk: true,
            CancellationToken.None);

        var now = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

        // Pass 1: only the access day is expired (change default is 365 days); its
        // aggregate lands in rollups/2026-08-01.jsonl and the change day is left.
        var accessOptions = new AuditOptions { AccessRetentionDays = 14, ChangeRetentionDays = 365 };
        await new AuditMaintenanceJob(store, accessOptions, new FixedTimeProvider(now)).RunAsync(CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(_root, "access", "2026-08-01.jsonl")));
        Assert.True(File.Exists(Path.Combine(_root, "changes", "2026-08-01.jsonl")));

        // Pass 2, months later: the change day finally expires (shorter window now).
        // Its aggregate must be merged alongside the access aggregate, not overwrite it.
        var changeOptions = new AuditOptions { AccessRetentionDays = 14, ChangeRetentionDays = 14 };
        await new AuditMaintenanceJob(store, changeOptions, new FixedTimeProvider(now)).RunAsync(CancellationToken.None);

        var rollupFile = Directory.GetFiles(Path.Combine(_root, "rollups"), "*.jsonl").Single();
        var rollups = File.ReadAllLines(rollupFile)
            .Select(l => JsonSerializer.Deserialize(l, AuditJsonContext.Default.AuditRollup)!)
            .ToList();
        Assert.Equal(2, rollups.Count);
        Assert.Contains(rollups, r => r.Trail == AuditTrail.Access && r.Action == "memory_read");
        Assert.Contains(rollups, r => r.Trail == AuditTrail.Change && r.Action == "memory_save");
        Assert.False(File.Exists(Path.Combine(_root, "changes", "2026-08-01.jsonl")));
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
