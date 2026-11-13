using Eling.Backend;
using Eling.Backend.Dtos;
using Eling.Backend.Services;
using Eling.Core.CommitLease;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// Unit tests for the push half of the commit-lease SSE channel: every
/// subscribing client is handed the live snapshot immediately, receives each
/// later publish in order, and (through the coordinator bridge) also receives
/// a fresh snapshot when another process relays a <c>commit-lease</c> topic
/// through <c>/api/coordinator/notify-change</c>.
/// </summary>
public sealed class CommitLeaseBroadcasterTests
{
    private readonly ITestOutputHelper _output;

    public CommitLeaseBroadcasterTests(ITestOutputHelper output) => _output = output;

    private static CommitLeaseSnapshotDto Snapshot(string tag) => new()
    {
        Checkout = "C:/checkout",
        Branch = "main",
        CommitLeases =
        [
            new CommitLeaseSummaryDto
            {
                LeaseId = "lease-" + tag,
                SessionId = "session-" + tag,
                Branch = "main",
                Since = new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero),
                LastHeartbeat = new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero),
                LeaseAgeSeconds = 0,
                TtlSeconds = 300,
                Paths = null,
                Intent = "intent-" + tag,
                Checkout = "C:/checkout",
            },
        ],
        RecentlyReleased = [],
    };

    private static async Task<CommitLeaseSnapshotDto> ReadFrameAsync(
        IAsyncEnumerator<CommitLeaseSnapshotDto> iterator,
        TimeSpan timeout)
    {
        var next = iterator.MoveNextAsync().AsTask();
        Assert.True(await next.WaitAsync(timeout), "SSE frame expected within the timeout.");
        return iterator.Current;
    }

    [Fact]
    public async Task SubscribeAsync_YieldsCurrentSnapshot_ImmediatelyOnConnect()
    {
        var broadcaster = new CommitLeaseBroadcaster(
            _ => Task.FromResult(Snapshot("initial")),
            NullLogger<CommitLeaseBroadcaster>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var iterator = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        var frame = await ReadFrameAsync(iterator, TimeSpan.FromSeconds(2));
        Assert.Equal("lease-initial", Assert.Single(frame.CommitLeases).LeaseId);
    }

    [Fact]
    public async Task Publish_ReachesSubscriber_AfterTheInitialSnapshot()
    {
        var broadcaster = new CommitLeaseBroadcaster(
            _ => Task.FromResult(Snapshot("initial")),
            NullLogger<CommitLeaseBroadcaster>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var iterator = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        _ = await ReadFrameAsync(iterator, TimeSpan.FromSeconds(2)); // initial frame

        broadcaster.Publish(Snapshot("pushed"));

        var frame = await ReadFrameAsync(iterator, TimeSpan.FromSeconds(2));
        Assert.Equal("lease-pushed", Assert.Single(frame.CommitLeases).LeaseId);
    }

    [Fact]
    public async Task NotifyAsync_PublishesAFreshSnapshot_LikeAStoreMutationWould()
    {
        var calls = 0;
        var broadcaster = new CommitLeaseBroadcaster(
            _ => Task.FromResult(Snapshot("v" + Interlocked.Increment(ref calls))),
            NullLogger<CommitLeaseBroadcaster>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var iterator = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        _ = await ReadFrameAsync(iterator, TimeSpan.FromSeconds(2)); // initial frame

        // The store calls NotifyAsync after each durable mutation.
        await broadcaster.NotifyAsync(cts.Token);

        var frame = await ReadFrameAsync(iterator, TimeSpan.FromSeconds(2));
        Assert.Equal("lease-v2", Assert.Single(frame.CommitLeases).LeaseId);
    }

    [Fact]
    public async Task CoordinatorBridge_CommitLeaseTopic_WakesSubscribers()
    {
        var topics = new MemoryChangeBroadcaster();
        var calls = 0;
        var broadcaster = new CommitLeaseBroadcaster(
            _ => Task.FromResult(Snapshot("v" + Interlocked.Increment(ref calls))),
            NullLogger<CommitLeaseBroadcaster>.Instance,
            topics);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var iterator = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        _ = await ReadFrameAsync(iterator, TimeSpan.FromSeconds(2)); // initial frame

        // The bridge subscribes from a background task; re-notify until the
        // relay lands (each publish is a full snapshot, so ordering stays sane).
        // One pending read is reused across attempts - concurrent MoveNextAsync
        // calls on an enumerator are not legal.
        var pending = iterator.MoveNextAsync().AsTask();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var woken = false;
        while (DateTime.UtcNow < deadline)
        {
            topics.Notify(HttpCoordinatorCommitLeaseChangeNotifier.LeaseTopic);
            if (await Task.WhenAny(pending, Task.Delay(250)) == pending)
            {
                woken = await pending;
                break;
            }
        }

        // Leave the enumerator with nothing in flight before the handles are
        // torn down, on every exit path: an async iterator whose MoveNextAsync
        // is pending cannot be disposed.
        cts.Cancel();
        if (!pending.IsCompleted)
        {
            try { await pending; }
            catch (OperationCanceledException ex) { _output.WriteLine($"Iterator ended on cancellation as expected: {ex.Message}"); }
        }

        Assert.True(woken, "A commit-lease coordinator topic never reached the subscriber within 5s.");
        Assert.NotEqual("lease-v1", Assert.Single(iterator.Current.CommitLeases).LeaseId);
    }

    [Fact]
    public async Task CoordinatorBridge_UnrelatedTopic_DoesNotWakeSubscribers()
    {
        var topics = new MemoryChangeBroadcaster();
        var calls = 0;
        var broadcaster = new CommitLeaseBroadcaster(
            _ => Task.FromResult(Snapshot("v" + Interlocked.Increment(ref calls))),
            NullLogger<CommitLeaseBroadcaster>.Instance,
            topics);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var iterator = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        _ = await ReadFrameAsync(iterator, TimeSpan.FromSeconds(2)); // initial frame

        // Unrelated topics must never rebuild the snapshot, whatever the
        // bridge has reached yet: the topic filter runs first.
        topics.Notify("memories");
        topics.Notify("runtimes");
        topics.Notify("codebase");
        await Task.Delay(300);
        Assert.Equal(1, calls); // initial frame only; no rebuild was triggered

        // Prove the bridge is live, then tear down with nothing in flight.
        // (Re-notify until its frame lands: the bridge subscribes from a
        // background task, so an early relay can be missed.)
        var pending = iterator.MoveNextAsync().AsTask();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var bridgedLive = false;
        while (DateTime.UtcNow < deadline)
        {
            topics.Notify(HttpCoordinatorCommitLeaseChangeNotifier.LeaseTopic);
            if (await Task.WhenAny(pending, Task.Delay(250)) == pending)
            {
                bridgedLive = true;
                break;
            }
        }

        cts.Cancel();
        try { await pending; }
        catch (OperationCanceledException ex) { _output.WriteLine($"Iterator ended on cancellation as expected: {ex.Message}"); }

        Assert.True(bridgedLive, "The bridge never relayed the commit-lease topic within 5s.");
    }
}
