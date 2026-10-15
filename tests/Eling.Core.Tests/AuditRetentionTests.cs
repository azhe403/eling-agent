using System.IO.Compression;
using System.Text;
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
    public async Task RunAsync_FoldsClosedDayBeforeItsWindowElapses()
    {
        // Amendment C3: the pass folds EVERY closed day, so the change trail has
        // an aggregate during its (long) retention window instead of only on the
        // day its raw file expires — and a raw file lost before its window
        // elapses still has its aggregate. Rolling up only expired days left
        // 365 days of the change trail with no aggregate at all.
        var day = new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 9, 11, 4, 0, 0, TimeSpan.Zero);
        var store = new AuditFileStore(_root, new FixedTimeProvider(day));
        await store.AppendAsync(
            AuditTrail.Change,
            [new AuditEvent { Timestamp = day, Trail = AuditTrail.Change, Category = AuditCategory.Memory, Action = "memory_save", Actor = "mcp", Outcome = AuditOutcome.Success }],
            flushToDisk: true,
            CancellationToken.None);

        var raw = store.GetCurrentFilePath(AuditTrail.Change, day);
        await new AuditMaintenanceJob(store, new AuditOptions(), new FixedTimeProvider(now)).RunAsync(CancellationToken.None);

        var rollupFile = Path.Combine(_root, "rollups", "2026-09-10.jsonl");
        Assert.True(File.Exists(rollupFile));
        var rollup = JsonSerializer.Deserialize(File.ReadAllLines(rollupFile).Single(), AuditJsonContext.Default.AuditRollup)!;
        Assert.Equal(AuditTrail.Change, rollup.Trail);
        Assert.Equal("memory_save", rollup.Action);
        Assert.Equal(1, rollup.Count);

        // The day stem is a UTC instant. Parsed with no styles it took the
        // machine's offset instead, so on this +07:00 box the folded date
        // compared seven hours off against the job's UTC-midnight clock — and
        // against every expiry window.
        Assert.Equal(TimeSpan.Zero, rollup.Date.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero), rollup.Date);

        // Folded is not expired: the raw day stays for the read API until its
        // window elapses.
        Assert.True(File.Exists(raw));
    }

    [Fact]
    public async Task RunAsync_DoesNotFoldTheDayStillInProgress()
    {
        // The in-progress day must clear the same closed-day gate on every
        // machine. A stem parsed with no styles takes the machine's offset, so
        // on a positive-offset one (this is +07:00) "today" lands at yesterday
        // 17:00 UTC — an instant the job already counts as closed, and it folds
        // the day into rollups/<today>.jsonl while it is still being appended to.
        var todayUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var clock = new FixedTimeProvider(todayUtc);
        var store = new AuditFileStore(_root, clock);
        await store.AppendAsync(
            AuditTrail.Change,
            [new AuditEvent { Timestamp = todayUtc, Trail = AuditTrail.Change, Category = AuditCategory.Memory, Action = "memory_save", Actor = "mcp", Outcome = AuditOutcome.Success }],
            flushToDisk: true,
            CancellationToken.None);

        var raw = store.GetCurrentFilePath(AuditTrail.Change, todayUtc);
        Assert.True(File.Exists(raw));

        await new AuditMaintenanceJob(store, new AuditOptions(), clock).RunAsync(CancellationToken.None);

        // No rollup at all for the day in progress, and the raw file survives for
        // the appends still to come.
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "rollups"), "*.jsonl"));
        Assert.True(File.Exists(raw));
    }

    [Fact]
    public async Task RunAsync_IgnoresStrayFilesThatAreNotDayFiles()
    {
        var day = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var store = new AuditFileStore(_root, new FixedTimeProvider(day));
        await store.AppendAsync(
            AuditTrail.Access,
            [new AuditEvent { Timestamp = day, Trail = AuditTrail.Access, Category = AuditCategory.Memory, Action = "memory_read", Actor = "mcp", Outcome = AuditOutcome.Success }],
            flushToDisk: true,
            CancellationToken.None);

        // A day file must end in exactly ".jsonl" or exactly ".jsonl.gz". Both of
        // these stem to the same date, but the reader decompresses only the
        // compound suffix, so they were read as raw text and the JSON parse
        // aborted the whole pass.
        var dir = Path.GetDirectoryName(store.GetCurrentFilePath(AuditTrail.Access, day))!;
        await File.WriteAllTextAsync(Path.Combine(dir, "2026-08-01.gz"), "not a gzip day file");
        await File.WriteAllTextAsync(Path.Combine(dir, "2026-08-01.json"), "not a jsonl day file");

        var options = new AuditOptions { AccessRetentionDays = 14 };
        var job = new AuditMaintenanceJob(store, options, new FixedTimeProvider(new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero)));

        await job.RunAsync(CancellationToken.None);

        var rollupFile = Directory.GetFiles(Path.Combine(_root, "rollups"), "*.jsonl").Single();
        var rollup = JsonSerializer.Deserialize(File.ReadAllLines(rollupFile).Single(), AuditJsonContext.Default.AuditRollup)!;
        Assert.Equal("memory_read", rollup.Action);
        Assert.Equal(1, rollup.Count);

        // Untouched: they are not day files, so the pass has no business with them.
        Assert.True(File.Exists(Path.Combine(dir, "2026-08-01.gz")));
        Assert.True(File.Exists(Path.Combine(dir, "2026-08-01.json")));
    }

    [Fact]
    public async Task RunAsync_RollupLine_UsesTheCanonicalKebabLabels()
    {
        var day = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var store = new AuditFileStore(_root, new FixedTimeProvider(day));
        await store.AppendAsync(
            AuditTrail.Access,
            [new AuditEvent { Timestamp = day, Trail = AuditTrail.Access, Category = AuditCategory.Filesystem, Action = "fs_read", Actor = "dashboard", Outcome = AuditOutcome.Success }],
            flushToDisk: true,
            CancellationToken.None);

        var options = new AuditOptions { AccessRetentionDays = 14 };
        var job = new AuditMaintenanceJob(store, options, new FixedTimeProvider(new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero)));

        await job.RunAsync(CancellationToken.None);

        var rollupFile = Directory.GetFiles(Path.Combine(_root, "rollups"), "*.jsonl").Single();
        var line = File.ReadAllLines(rollupFile).Single();

        // A rollup is retained indefinitely (§13), so its labels are settled at
        // first write: the same kebab spellings the JSONL carries, never the
        // PascalCase enum names ("Filesystem"/"Success") the rollup used to
        // write (amendment C1).
        Assert.Contains("\"trail\":\"access\"", line);
        Assert.Contains("\"category\":\"filesystem\"", line);
        Assert.Contains("\"outcome\":\"success\"", line);
    }

    [Fact]
    public async Task RunAsync_WhenBothDayFormsExist_FoldsTheDayOnce()
    {
        var day = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var store = new AuditFileStore(_root, new FixedTimeProvider(day));
        await store.AppendAsync(
            AuditTrail.Access,
            [new AuditEvent { Timestamp = day, Trail = AuditTrail.Access, Category = AuditCategory.Memory, Action = "memory_read", Actor = "mcp", Outcome = AuditOutcome.Success }],
            flushToDisk: true,
            CancellationToken.None);

        // Simulate a rotation that crashed between writing the .gz and deleting
        // the raw, so the day exists in both forms. The compressed form wins and
        // the day is counted once, not twice.
        var raw = store.GetCurrentFilePath(AuditTrail.Access, day);
        var content = await File.ReadAllTextAsync(raw);
        await using (var input = new MemoryStream(Encoding.UTF8.GetBytes(content)))
        await using (var output = File.Create(raw + ".gz"))
        await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        {
            await input.CopyToAsync(gzip);
        }

        var options = new AuditOptions { AccessRetentionDays = 14 };
        var job = new AuditMaintenanceJob(store, options, new FixedTimeProvider(new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero)));

        await job.RunAsync(CancellationToken.None);

        var rollupFile = Directory.GetFiles(Path.Combine(_root, "rollups"), "*.jsonl").Single();
        var rollup = JsonSerializer.Deserialize(File.ReadAllLines(rollupFile).Single(), AuditJsonContext.Default.AuditRollup)!;
        Assert.Equal(1, rollup.Count);
        Assert.False(File.Exists(raw + ".gz"));
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
