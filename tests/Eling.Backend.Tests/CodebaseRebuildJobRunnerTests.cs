using Eling.Backend.Codebase;
using Eling.Backend.Dtos;
using Eling.Core.Codebase;
using Eling.Core.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// Covers the rebuild job's observable contract: which roots it touches, in
/// which mode, how a failing root is isolated, and that a second start joins
/// the running job instead of starting a competing pass over the same DBs.
/// The reindex seam keeps every test off disk.
/// </summary>
public sealed class CodebaseRebuildJobRunnerTests
{
    private const string RootA = @"C:\repos\alpha";
    private const string RootB = @"C:\repos\beta";
    private const string RootC = @"C:\repos\gamma";

    private sealed record RecordedCall(string Root, bool Full);

    [Fact]
    public async Task Start_SingleTarget_IndexesOnlyThatRoot()
    {
        var calls = new List<RecordedCall>();
        var runner = CreateRunner((root, full) =>
        {
            calls.Add(new RecordedCall(root, full));
            return Task.FromResult(Success());
        });

        var started = runner.Start(Request(RootA));
        var done = await WaitForCompletion(runner, started.JobId);

        Assert.Single(calls);
        Assert.Equal(RootA, calls[0].Root);
        Assert.Equal(1, done.Done);
        Assert.False(done.IsRunning);
        Assert.Null(done.Error);
    }

    [Fact]
    public async Task Start_MultipleTargets_IndexesEveryRootInOrder()
    {
        var calls = new List<RecordedCall>();
        var runner = CreateRunner((root, full) =>
        {
            calls.Add(new RecordedCall(root, full));
            return Task.FromResult(Success());
        });

        var started = runner.Start(Request(RootA, RootB, RootC));
        var done = await WaitForCompletion(runner, started.JobId);

        Assert.Equal([RootA, RootB, RootC], calls.Select(c => c.Root));
        Assert.Equal(3, done.Total);
        Assert.Equal(3, done.Done);
        Assert.All(done.Results, r => Assert.True(r.Ok));
    }

    [Fact]
    public async Task Start_FullMode_ThreadsFullFlagIntoEveryPass()
    {
        var calls = new List<RecordedCall>();
        var runner = CreateRunner((root, full) =>
        {
            calls.Add(new RecordedCall(root, full));
            return Task.FromResult(Success());
        });

        var started = runner.Start(new CodebaseRebuildRequest("projects", [RootA, RootB], [], Full: true));
        await WaitForCompletion(runner, started.JobId);

        Assert.All(calls, c => Assert.True(c.Full));
        Assert.True(started.Full);
    }

    [Fact]
    public async Task Start_IncrementalMode_ThreadsFullFalseIntoEveryPass()
    {
        var calls = new List<RecordedCall>();
        var runner = CreateRunner((root, full) =>
        {
            calls.Add(new RecordedCall(root, full));
            return Task.FromResult(Success());
        });

        var started = runner.Start(new CodebaseRebuildRequest("projects", [RootA], [], Full: false));
        await WaitForCompletion(runner, started.JobId);

        Assert.All(calls, c => Assert.False(c.Full));
    }

    [Fact]
    public async Task Start_OneRootThrows_RecordsFailureAndStillFinishesTheOthers()
    {
        var calls = new List<RecordedCall>();
        var runner = CreateRunner((root, full) =>
        {
            calls.Add(new RecordedCall(root, full));
            return root == RootB
                ? Task.FromException<CodebaseIndexResult>(new InvalidOperationException("database is locked"))
                : Task.FromResult(Success());
        });

        var started = runner.Start(Request(RootA, RootB, RootC));
        var done = await WaitForCompletion(runner, started.JobId);

        Assert.Equal(3, calls.Count);
        Assert.False(done.IsRunning);
        Assert.Null(done.Error);

        var failed = Assert.Single(done.Results, r => r.ProjectRoot == RootB);
        Assert.False(failed.Ok);
        Assert.Equal("database is locked", failed.Error);
        Assert.Equal(0, failed.Files);

        Assert.True(Assert.Single(done.Results, r => r.ProjectRoot == RootA).Ok);
        Assert.True(Assert.Single(done.Results, r => r.ProjectRoot == RootC).Ok);
    }

    [Fact]
    public async Task Start_WhileJobRunning_JoinsTheSameJob()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var runner = CreateRunner(async (root, full) =>
        {
            entered.SetResult();
            await release.Task;
            return Success();
        });

        var first = runner.Start(Request(RootA));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = runner.Start(Request(RootB));

        Assert.Equal(first.JobId, second.JobId);
        Assert.True(second.IsRunning);

        release.SetResult();
        var done = await WaitForCompletion(runner, first.JobId);
        Assert.Equal(1, done.Total);
    }

    [Fact]
    public async Task Start_AfterCompletion_StartsAFreshJob()
    {
        var calls = new List<RecordedCall>();
        var runner = CreateRunner((root, full) =>
        {
            calls.Add(new RecordedCall(root, full));
            return Task.FromResult(Success());
        });

        var first = runner.Start(Request(RootA));
        await WaitForCompletion(runner, first.JobId);
        var second = runner.Start(Request(RootB));
        await WaitForCompletion(runner, second.JobId);

        Assert.NotEqual(first.JobId, second.JobId);
        Assert.Equal([RootA, RootB], calls.Select(c => c.Root));
    }

    [Fact]
    public async Task Start_NoTargets_CompletesWithNothingIndexed()
    {
        var calls = new List<RecordedCall>();
        var runner = CreateRunner((root, full) =>
        {
            calls.Add(new RecordedCall(root, full));
            return Task.FromResult(Success());
        });

        var started = runner.Start(new CodebaseRebuildRequest("all", [], [RootB, RootC], Full: false));
        var done = await WaitForCompletion(runner, started.JobId);

        Assert.Empty(calls);
        Assert.Empty(done.Results);
        Assert.Equal(0, done.Total);
        Assert.Equal([RootB, RootC], done.SkippedRoots);
        Assert.False(done.IsRunning);
    }

    [Fact]
    public void Get_UnknownJobId_ReturnsNull()
    {
        var runner = CreateRunner((root, full) => Task.FromResult(Success()));

        Assert.Null(runner.Get("not-a-job"));
        Assert.Null(runner.Get(""));
    }

    private static CodebaseRebuildRequest Request(params string[] targets) =>
        new("projects", targets, [], Full: false);

    private static CodebaseIndexResult Success() => new(7, 9, 3, 1, "test.db");

    private static CodebaseRebuildJobRunner CreateRunner(
        Func<string, bool, Task<CodebaseIndexResult>> reindex)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "eling-rebuild-runner");
        var local = new CodebaseIndexService(
            scratch,
            new SqliteCodebaseIndex(Path.Combine(scratch, "local.db")));

        return new CodebaseRebuildJobRunner(
            local,
            new CodebaseRebuildBroadcaster(),
            NullMemoryChangeNotifier.Instance,
            NullLogger<CodebaseRebuildJobRunner>.Instance,
            lifetime: null,
            reindex: (root, full, _) => reindex(root, full));
    }

    private static async Task<CodebaseRebuildProgress> WaitForCompletion(
        CodebaseRebuildJobRunner runner,
        string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = runner.Get(jobId);
            if (snapshot is not null && !snapshot.IsRunning) return snapshot;
            await Task.Delay(10);
        }

        throw new TimeoutException("Codebase rebuild job did not finish within 30s.");
    }
}
