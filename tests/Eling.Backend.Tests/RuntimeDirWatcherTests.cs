namespace Eling.Backend.Tests;

public sealed class RuntimeDirWatcherTests : IDisposable
{
    private readonly string _dir;
    private readonly MemoryChangeBroadcaster _broadcaster = new();

    public RuntimeDirWatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "eling-watcher-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task FileCreated_NotifiesRuntimes()
    {
        using var watcher = new RuntimeDirWatcher(_dir, _broadcaster);
        watcher.Start();

        File.WriteAllText(Path.Combine(_dir, "1001.json"), "{}");

        Assert.Equal("runtimes", await NextEventAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task FileDeleted_NotifiesRuntimes()
    {
        var path = Path.Combine(_dir, "1002.json");
        File.WriteAllText(path, "{}");

        using var watcher = new RuntimeDirWatcher(_dir, _broadcaster);
        watcher.Start();

        File.Delete(path);

        Assert.Equal("runtimes", await NextEventAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task FileRenamed_NotifiesRuntimes()
    {
        var source = Path.Combine(_dir, "1003.json");
        File.WriteAllText(source, "{}");

        using var watcher = new RuntimeDirWatcher(_dir, _broadcaster);
        watcher.Start();

        File.Move(source, Path.Combine(_dir, "1004.json"));

        Assert.Equal("runtimes", await NextEventAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TimestampTouch_DoesNotNotify()
    {
        var path = Path.Combine(_dir, "1005.json");
        File.WriteAllText(path, "{}");

        using var watcher = new RuntimeDirWatcher(_dir, _broadcaster);
        watcher.Start();

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

        Assert.Null(await NextEventAsync(TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public async Task Dispose_StopsNotifications()
    {
        var watcher = new RuntimeDirWatcher(_dir, _broadcaster);
        watcher.Start();
        watcher.Dispose();

        File.WriteAllText(Path.Combine(_dir, "1006.json"), "{}");

        Assert.Null(await NextEventAsync(TimeSpan.FromSeconds(1.5)));
    }

    private async Task<string?> NextEventAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var evt in _broadcaster.SubscribeAsync(cts.Token))
            {
                return evt;
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout expired without any event.
        }

        return null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
