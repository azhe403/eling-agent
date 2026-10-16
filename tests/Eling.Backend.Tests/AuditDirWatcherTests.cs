using Eling.Backend;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The store watcher behind the per-trail live topics: an append to a
/// trail's day file broadcasts that trail's topic, debounced, and nothing
/// else does. The dashboard subscribes to exactly the trail it displays, so
/// a broadcast always means "your trail grew".
/// </summary>
public sealed class AuditDirWatcherTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eling-audit-watch-" + Guid.NewGuid().ToString("N"));

    private MemoryChangeBroadcaster _broadcaster = null!;
    private AuditDirWatcher _watcher = null!;

    public Task InitializeAsync()
    {
        _broadcaster = new MemoryChangeBroadcaster();
        _watcher = new AuditDirWatcher(_root, _broadcaster);
        _watcher.Start();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _watcher.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AppendToChanges_BroadcastsChangesTopic()
    {
        var received = ReceiveFirstAsync(TimeSpan.FromSeconds(10));

        await File.AppendAllTextAsync(
            Path.Combine(_root, "changes", "2026-10-16.jsonl"),
            "{\"action\":\"memory_save\"}\n");

        Assert.Equal(AuditDirWatcher.ChangesTopic, await received);
    }

    [Fact]
    public async Task AppendToAccess_BroadcastsAccessTopic()
    {
        var received = ReceiveFirstAsync(TimeSpan.FromSeconds(10));

        await File.AppendAllTextAsync(
            Path.Combine(_root, "access", "2026-10-16.jsonl"),
            "{\"action\":\"memory_search\"}\n");

        Assert.Equal(AuditDirWatcher.AccessTopic, await received);
    }

    [Fact]
    public async Task RotatedGzArrival_BroadcastsItsTrailTopic()
    {
        // Rotation compresses a closed day and deletes the raw file: the
        // arrival of the .gz must still wake a subscriber of that trail.
        var received = ReceiveFirstAsync(TimeSpan.FromSeconds(10));

        await File.WriteAllBytesAsync(
            Path.Combine(_root, "changes", "2026-10-15.jsonl.gz"),
            [0x1f, 0x8b, 0x08, 0x00]);

        Assert.Equal(AuditDirWatcher.ChangesTopic, await received);
    }

    private async Task<string> ReceiveFirstAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var item in _broadcaster.SubscribeAsync(cts.Token))
        {
            return item;
        }

        throw new TimeoutException("No audit broadcast arrived within the timeout.");
    }
}
