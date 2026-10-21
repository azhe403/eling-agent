using Eling.Backend;

namespace Eling.Backend.Tests;

public sealed class DatabaseRegistryWatcherTests : IDisposable
{
    private readonly string _dir;

    public DatabaseRegistryWatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"eling-dbwatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must not fail the test run.
        }
    }

    [Fact]
    public async Task RegistryWrite_BroadcastsTheDatabasesTopic()
    {
        // Arrange
        var broadcaster = new MemoryChangeBroadcaster();
        using var watcher = new DatabaseRegistryWatcher(_dir, broadcaster);
        watcher.Start();

        // Act - the store writes a temp file and moves it over the registry.
        var topic = await TryReceiveAsync(
            broadcaster,
            () =>
            {
                var temp = Path.Combine(_dir, "databases.json.tmp");
                File.WriteAllText(temp, "[]");
                File.Move(temp, Path.Combine(_dir, "databases.json"), overwrite: true);
            },
            TimeSpan.FromSeconds(15));

        // Assert
        Assert.Equal("databases", topic);
    }

    [Fact]
    public async Task UnrelatedFile_Write_BroadcastsNothing()
    {
        // Arrange
        var broadcaster = new MemoryChangeBroadcaster();
        using var watcher = new DatabaseRegistryWatcher(_dir, broadcaster);
        watcher.Start();

        // Act - a different file in the same directory is not the registry.
        var topic = await TryReceiveAsync(
            broadcaster,
            () => File.WriteAllText(Path.Combine(_dir, "something-else.txt"), "hello"),
            TimeSpan.FromSeconds(2));

        // Assert
        Assert.Null(topic);
    }

    /// <summary>
    /// Subscribes, runs <paramref name="afterSubscribe"/>, and returns the first
    /// topic that arrives — or null when none does before the timeout.
    /// </summary>
    /// <remarks>
    /// The subscription is registered synchronously by the first
    /// <c>MoveNextAsync</c> call, so the action runs with the subscriber
    /// already listening. A pending read is cancelled and drained before the
    /// iterator is disposed, because disposing an async iterator with a move in
    /// flight surfaces that move's cancellation as an exception.
    /// </remarks>
    private static async Task<string?> TryReceiveAsync(
        MemoryChangeBroadcaster broadcaster,
        Action afterSubscribe,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var enumerator = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Task<bool>? pending = null;
        try
        {
            pending = enumerator.MoveNextAsync().AsTask();
            afterSubscribe();

            var winner = await Task.WhenAny(pending, Task.Delay(timeout));
            if (!ReferenceEquals(winner, pending))
            {
                return null;
            }

            await pending;
            return enumerator.Current;
        }
        finally
        {
            await cts.CancelAsync();
            if (pending is not null && !pending.IsCompleted)
            {
                try { await pending; } catch (OperationCanceledException) { }
            }
            try { await enumerator.DisposeAsync(); } catch (OperationCanceledException) { }
        }
    }
}
