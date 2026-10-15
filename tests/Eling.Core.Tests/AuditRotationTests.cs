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
        var day1 = new DateTimeOffset(2026, 9, 10, 23, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddHours(2);
        var store = new AuditFileStore(_root, new FixedTimeProvider(day1));
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
        var day = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        var store = new AuditFileStore(_root, new FixedTimeProvider(day));
        await store.AppendAsync(AuditTrail.Change, [new AuditEvent { Timestamp = day, Action = "memory_save" }], true, CancellationToken.None);

        var rotated = await AuditRotation.RotateIfNeededAsync(store, AuditTrail.Change, day.AddMinutes(30), compress: true, CancellationToken.None);

        Assert.True(File.Exists(rotated));
        Assert.False(File.Exists(Path.Combine(_root, "changes", "2026-09-11.jsonl.gz")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // Nested rather than a second top-level type: a file-scoped helper would
    // break CodeOrganizationConventionTests' one-type-per-file rule, which
    // this repo tracks.
    internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
