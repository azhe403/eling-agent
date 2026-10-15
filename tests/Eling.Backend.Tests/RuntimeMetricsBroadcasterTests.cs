using Eling.Backend.Dtos;
using Eling.Backend.Services;
using Eling.Core.Projects;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The broadcaster owns the one lifecycle rule the metrics stream depends on:
/// the pump runs exactly while somebody is watching. Fan-out itself is the
/// same unbounded-channel pattern as CodebaseRebuildBroadcaster.
/// </summary>
public sealed class RuntimeMetricsBroadcasterTests : IDisposable
{
    private readonly List<RuntimeRegistry> _registries = [];

    private RuntimeMetricsBroadcaster NewBroadcaster(
        Func<IReadOnlyList<RuntimeMetricsSnapshot>>? sampleFactory = null,
        TimeSpan? interval = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "eling-bcast-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var dataDir = Path.Combine(root, "data");
        var userScope = UserScope.Resolve(Path.Combine(dataDir, "user-scope"));
        var workspaces = new JsonWorkspaceRegistry(Path.Combine(dataDir, "agent-workspaces.json"));
        var registry = new RuntimeRegistry(
            NullLogger<RuntimeRegistry>.Instance, userScope, workspaces: workspaces);
        _registries.Add(registry);

        var sampler = new RuntimeMetricsSampler(
            registry, new ProcessMetricsTracker(NullLogger<ProcessMetricsTracker>.Instance), null);
        return new RuntimeMetricsBroadcaster(
            sampler, NullLogger<RuntimeMetricsBroadcaster>.Instance, sampleFactory, interval);
    }

    [Fact]
    public async Task Publish_TwoSubscribers_BothReceiveTheSameBatch()
    {
        // Park the pump inside its first sample: until the gate opens, the
        // only data in flight is the sentinel below. That removes tick noise
        // and frame-order races — the test is exactly "one Publish reaches
        // every subscriber that has registered".
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broadcaster = NewBroadcaster(
            sampleFactory: () =>
            {
                started.TrySetResult();
                gate.Task.GetAwaiter().GetResult();
                return new[] { new RuntimeMetricsSnapshot(777, 1024, 0, DateTimeOffset.UtcNow) };
            },
            interval: TimeSpan.FromMilliseconds(20));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var s1 = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator();
        await using var s2 = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator();

        // Async iterators are lazy: the channel registers when the first
        // MoveNext runs the body synchronously up to the first await. Kick
        // both off (without awaiting) so both are registered subscribers.
        var first1 = s1.MoveNextAsync().AsTask();
        var first2 = s2.MoveNextAsync().AsTask();
        await started.Task;

        var sentinel = new[] { new RuntimeMetricsSnapshot(999, 2048, 12.5, DateTimeOffset.UtcNow) };
        broadcaster.Publish(sentinel);

        Assert.True(await first1);
        Assert.True(await first2);
        Assert.Equal(999, s1.Current[0].Pid);
        Assert.Equal(999, s2.Current[0].Pid);

        gate.SetResult(); // let the parked pump wind down with the enumerators
    }

    [Fact]
    public void Publish_NoSubscribers_DoesNotThrow()
    {
        var broadcaster = NewBroadcaster();

        broadcaster.Publish(new[] { new RuntimeMetricsSnapshot(1, 1, 0, DateTimeOffset.UtcNow) });
    }

    [Fact]
    public async Task Pump_Subscribed_DeliversTicks_StopsWhenLastLeaves_RestartsOnResubscribe()
    {
        var calls = 0;
        var broadcaster = NewBroadcaster(
            sampleFactory: () =>
            {
                calls++;
                return new[] { new RuntimeMetricsSnapshot(777, 4096, 0, DateTimeOffset.UtcNow) };
            },
            interval: TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var stream = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.True(await stream.MoveNextAsync());
        Assert.True(calls >= 2, $"expected the pump to tick at least twice, got {calls}");

        await stream.DisposeAsync();
        var atDispose = calls;
        await Task.Delay(300, CancellationToken.None);
        Assert.Equal(atDispose, calls);

        // The gate: a fresh subscriber restarts the pump instead of leaving a
        // connected stream that never publishes again.
        var second = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator();
        Assert.True(await second.MoveNextAsync());
        await second.DisposeAsync();
        Assert.True(calls >= atDispose + 1, $"expected the pump to restart, calls stayed at {calls}");
    }

    public void Dispose()
    {
        foreach (var registry in _registries) registry.Dispose();
        GC.SuppressFinalize(this);
    }
}
