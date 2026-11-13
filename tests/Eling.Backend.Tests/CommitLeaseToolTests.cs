using System.Diagnostics;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Git;
using Eling.Core.CommitLease;

namespace Eling.Backend.Tests;

public sealed class CommitLeaseToolTests : IDisposable
{
    private readonly string _root;
    private readonly ManualTimeProvider _clock;

    public CommitLeaseToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-commit-lease-tool-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        // git object files are created read-only; Directory.Delete(recursive)
        // cannot remove them on Windows, so clear the attribute first.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Could not clear read-only on {file}: {ex.Message}");
            }
        }
        Directory.Delete(_root, recursive: true);
    }

    private string Checkout(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private JsonCommitLeaseStore NewStore()
        => new(Path.Combine(_root, "commit-leases.json"), notifier: null, timeProvider: _clock);

    private async Task<string> InitGitRepo(string name, string branch = "main")
    {
        var dir = Checkout(name);
        await RunGit(dir, "init", "-b", branch);
        return dir;
    }

    private static async Task<string> RunGit(string workingDirectory, params string[] args)
    {
        var result = await Cli.Wrap("git").WithEnvironmentVariables(b => b.Set("GIT_DIR", ".git").Set("GIT_WORK_TREE", "."))
            .WithArguments(args)
            .WithWorkingDirectory(workingDirectory)
            .ExecuteBufferedAsync();
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)} exited with {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput;
    }

    private CommitLeaseTool NewTool(JsonCommitLeaseStore store, string cwd)
        => new(store, _clock, cwd);

    [Fact]
    public async Task FirstLease_Succeeds_AndReturnsLeaseId()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", intent: "land the feature", paths: ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        var leaseId = doc.RootElement.GetProperty("lease_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(leaseId));
        Assert.Equal(_clock.GetUtcNow(), doc.RootElement.GetProperty("since").GetDateTimeOffset());
        var stored = Assert.Single(await store.ListAllAsync());
        Assert.Equal(leaseId, stored.LeaseId);
        Assert.Equal(CommitLeaseKind.Commit, stored.Kind);
        Assert.Equal(cwd, stored.Checkout);
    }

    [Fact]
    public async Task SecondLease_SameCheckoutBranch_ReturnsHolderInfo_AndDoesNotOverwrite()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var first = NewTool(store, cwd);
        var firstResult = await first.LeaseAsync("ses_test_1_000000000000000000000", "main", intent: "first in line");
        using var firstDoc = JsonDocument.Parse(firstResult);
        var firstLeaseId = firstDoc.RootElement.GetProperty("lease_id").GetString();
        _clock.Advance(TimeSpan.FromSeconds(30));

        var second = NewTool(store, cwd);
        var result = await second.LeaseAsync("ses_test_2_000000000000000000000", "main", intent: "me too");

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("ses_test_1_000000000000000000000", doc.RootElement.GetProperty("holder_session_id").GetString());
        Assert.Equal(30, doc.RootElement.GetProperty("lease_age_seconds").GetInt64());
        Assert.Equal("first in line", doc.RootElement.GetProperty("intent").GetString());
        var stored = Assert.Single(await store.ListAllAsync());
        Assert.Equal(firstLeaseId, stored.LeaseId);
        Assert.Equal("ses_test_1_000000000000000000000", stored.SessionId);
    }

    [Fact]
    public async Task Lease_OnDifferentCheckout_Succeeds()
    {
        using var store = NewStore();
        var toolA = NewTool(store, Checkout("a"));
        var toolB = NewTool(store, Checkout("b"));

        var resultA = await toolA.LeaseAsync("ses_test_1_000000000000000000000", "main");
        var resultB = await toolB.LeaseAsync("ses_test_2_000000000000000000000", "main");

        using var docA = JsonDocument.Parse(resultA);
        using var docB = JsonDocument.Parse(resultB);
        Assert.False(string.IsNullOrWhiteSpace(docA.RootElement.GetProperty("lease_id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(docB.RootElement.GetProperty("lease_id").GetString()));
        Assert.NotEqual(
            docA.RootElement.GetProperty("lease_id").GetString(),
            docB.RootElement.GetProperty("lease_id").GetString());
        Assert.Equal(2, (await store.ListAllAsync()).Count);
    }

    [Fact]
    public async Task Lease_FromMainCheckoutAndLinkedWorktree_ProduceDistinctLeaseKeys()
    {
        using var store = NewStore();
        var main = await InitGitRepo("main");
        // A linked worktree is a directory with a .git FILE pointing at
        // <main>/.git/worktrees/<name>, and that directory carries a commondir
        // file naming the main repository's .git. The resolver reads only this
        // marker; creating the fixture by hand keeps the test independent of
        // git's checkout stage, which is heavy (and occasionally refuses) under
        // a fully-parallel test host.
        var worktree = Path.Combine(_root, "wt");
        var worktreeGit = Path.Combine(main, ".git", "worktrees", "wt-branch");
        Directory.CreateDirectory(worktreeGit);
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGit}");

        var mainTool = NewTool(store, main);
        var worktreeTool = NewTool(store, worktree);
        var mainResult = await mainTool.LeaseAsync("ses_test_1_000000000000000000000", "main", intent: "main in flight");
        var worktreeResult = await worktreeTool.LeaseAsync("ses_test_2_000000000000000000000", "wt-branch", intent: "worktree in flight");

        using var mainDoc = JsonDocument.Parse(mainResult);
        using var worktreeDoc = JsonDocument.Parse(worktreeResult);
        // Both leases are taken: the worktree keys by its own path, so it does
        // not collide with the main checkout's slot.
        Assert.True(mainDoc.RootElement.TryGetProperty("lease_id", out _), $"main lease was not taken: {mainResult}");
        Assert.True(worktreeDoc.RootElement.TryGetProperty("lease_id", out _), $"worktree lease was not taken: {worktreeResult}");
        Assert.False(worktreeDoc.RootElement.TryGetProperty("holder_session_id", out _));

        var stored = await store.ListAllAsync();
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, l => l.SessionId == "ses_test_1_000000000000000000000" && l.Checkout == GitRepositoryResolver.ResolveWorkingCopyRoot(main));
        Assert.Contains(stored, l => l.SessionId == "ses_test_2_000000000000000000000" && l.Checkout == GitRepositoryResolver.ResolveWorkingCopyRoot(worktree));
        Assert.NotEqual(stored[0].Checkout, stored[1].Checkout);
    }

    [Fact]
    public async Task PathsAndIntent_RoundTripIntoStoredRecord()
    {
        using var store = NewStore();
        var tool = NewTool(store, Checkout("a"));

        await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", intent: "land the feature", paths: ["src/a.cs", "src/b.cs"]);

        var stored = Assert.Single(await store.ListAllAsync());
        Assert.Equal(new[] { "src/a.cs", "src/b.cs" }, stored.Paths);
        Assert.Equal("land the feature", stored.Intent);
    }

    [Fact]
    public async Task BlankBranch_ReturnsError_AndStoresNothing()
    {
        using var store = NewStore();
        var tool = NewTool(store, Checkout("a"));

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "   ");

        using var doc = JsonDocument.Parse(result);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
        Assert.Empty(await store.ListAllAsync());
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
