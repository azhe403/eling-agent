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
        Assert.Equal("ses_test_1_000000000000000000000", doc.RootElement.GetProperty("holder_caller_id").GetString());
        Assert.Equal(30, doc.RootElement.GetProperty("lease_age_seconds").GetInt64());
        Assert.Equal("first in line", doc.RootElement.GetProperty("intent").GetString());
        var stored = Assert.Single(await store.ListAllAsync());
        Assert.Equal(firstLeaseId, stored.LeaseId);
        Assert.Equal("ses_test_1_000000000000000000000", stored.CallerId);
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
        Assert.False(worktreeDoc.RootElement.TryGetProperty("holder_caller_id", out _));

        var stored = await store.ListAllAsync();
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, l => l.CallerId == "ses_test_1_000000000000000000000" && l.Checkout == GitRepositoryResolver.ResolveWorkingCopyRoot(main));
        Assert.Contains(stored, l => l.CallerId == "ses_test_2_000000000000000000000" && l.Checkout == GitRepositoryResolver.ResolveWorkingCopyRoot(worktree));
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
    public async Task BlankBranch_FallsBackToFailSafeScope_LikeTheSiblingTools()
    {
        using var store = NewStore();
        var cwd = Checkout("a"); // no git init: branch is undiscoverable, so a foreign lease on any branch blocks
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "main", intent: "holds the slot");
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "   ");

        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal("red", doc.RootElement.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task OwnLease_Reentry_WithSameIntent_IsIdempotent()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        var first = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", intent: "land the fix");
        var second = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", intent: "land the fix");

        using var firstDoc = JsonDocument.Parse(first);
        using var secondDoc = JsonDocument.Parse(second);
        Assert.True(firstDoc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.True(secondDoc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal(firstDoc.RootElement.GetProperty("lease_id").GetString(), secondDoc.RootElement.GetProperty("lease_id").GetString());
        Assert.Equal(firstDoc.RootElement.GetProperty("since").GetDateTimeOffset(), secondDoc.RootElement.GetProperty("since").GetDateTimeOffset());
    }

    [Fact]
    public async Task OwnLease_Reentry_WithDifferentIntent_ReturnsIntentMismatch()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        var first = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", intent: "first task");
        var second = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", intent: "second task");

        using var firstDoc = JsonDocument.Parse(first);
        using var secondDoc = JsonDocument.Parse(second);
        Assert.True(firstDoc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.False(secondDoc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Contains("intent_mismatch", secondDoc.RootElement.GetProperty("error").GetString());
        Assert.Equal(firstDoc.RootElement.GetProperty("lease_id").GetString(), secondDoc.RootElement.GetProperty("lease_id").GetString());
        Assert.Equal("first task", secondDoc.RootElement.GetProperty("held_intent").GetString());
        Assert.Equal("second task", secondDoc.RootElement.GetProperty("requested_intent").GetString());
        // lease unchanged: still only one lease in the store
        Assert.Single(await store.ListAllAsync());
    }

    [Fact]
    public async Task OwnLease_Reentry_WithAbsentIntent_StillIdempotent()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        var first = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", intent: "first task");
        var second = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main");

        using var firstDoc = JsonDocument.Parse(first);
        using var secondDoc = JsonDocument.Parse(second);
        Assert.True(firstDoc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.True(secondDoc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal(firstDoc.RootElement.GetProperty("lease_id").GetString(), secondDoc.RootElement.GetProperty("lease_id").GetString());
    }

    [Fact]
    public async Task OwnLease_Reentry_NullPreviousIntent_SameIntentRequest_Acquires()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        // lease created without intent; second call with intent is treated as same-slot re-entry (held intent absent, no contradiction)
        await tool.LeaseAsync("ses_test_1_000000000000000000000", "main");
        var second = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", intent: "landing the fix");

        using var doc = JsonDocument.Parse(second);
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean());
    }

    [Fact]
    public async Task OwnLease_Reentry_WithDifferentIntent_AfterRelease_AcquiresFresh()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        var first = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", intent: "first task");
        var leaseToken = JsonDocument.Parse(first).RootElement.GetProperty("lease_token").GetString();
        await store.ReleaseAsync("ses_test_1_000000000000000000000", CommitLeaseKind.Commit, leaseToken);
        var third = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", intent: "second task");

        using var doc = JsonDocument.Parse(third);
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean());
    }

    [Fact]
    public async Task OwnLease_Reentry_IsIdempotent()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        var first = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main");
        var second = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main");

        using var firstDoc = JsonDocument.Parse(first);
        using var secondDoc = JsonDocument.Parse(second);
        Assert.True(firstDoc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.True(secondDoc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal(firstDoc.RootElement.GetProperty("lease_id").GetString(), secondDoc.RootElement.GetProperty("lease_id").GetString());
        Assert.Equal(firstDoc.RootElement.GetProperty("since").GetDateTimeOffset(), secondDoc.RootElement.GetProperty("since").GetDateTimeOffset());
        Assert.Equal("green", secondDoc.RootElement.GetProperty("verdict").GetString());
        Assert.False(secondDoc.RootElement.GetProperty("staged_without_lease").GetBoolean());
    }

    [Fact]
    public async Task CleanTree_NoLeases_AcquiresAndReturnsGreen()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", paths: ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
        Assert.False(doc.RootElement.GetProperty("staged_without_lease").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("holder_caller_id", out _));
        Assert.False(doc.RootElement.TryGetProperty("conflicting_file_leases", out _));
    }

    [Fact]
    public async Task ForeignCommitLease_SameCheckoutAndBranch_DoesNotAcquire_AndReportsHolder()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "main", intent: "land the feature");
        _clock.Advance(TimeSpan.FromSeconds(45));
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", paths: ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal("red", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("ses_test_2_000000000000000000000", doc.RootElement.GetProperty("holder_caller_id").GetString());
        Assert.Equal(45, doc.RootElement.GetProperty("lease_age_seconds").GetInt64());
        Assert.Equal("land the feature", doc.RootElement.GetProperty("intent").GetString());
        Assert.False(doc.RootElement.GetProperty("staged_without_lease").GetBoolean());
        var stored = Assert.Single(await store.ListAllAsync());
        Assert.Equal("ses_test_2_000000000000000000000", stored.CallerId);
    }

    [Fact]
    public async Task ForeignCommitLease_OnOtherBranch_AcquiresGreen()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a", branch: "main");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "develop", intent: "other branch work");
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", paths: ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task NonGitDirectory_ForeignCommitLease_BlocksAnyBranch()
    {
        using var store = NewStore();
        var cwd = Checkout("a"); // no git init: branch is undiscoverable, so the fail-safe scope is any branch of this checkout
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "main", intent: "holds the slot");
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", branch: null, paths: ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal("red", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("ses_test_2_000000000000000000000", doc.RootElement.GetProperty("holder_caller_id").GetString());
    }

    [Fact]
    public async Task StagedFiles_CallerHoldsNoLease_VerdictGreen_AndWarns()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        File.WriteAllText(Path.Combine(cwd, "staged.cs"), "class Staged { }");
        await RunGit(cwd, "add", "staged.cs");
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", paths: ["staged.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("staged_without_lease").GetBoolean());
    }

    [Fact]
    public async Task StagedFiles_CallerHoldsOwnLease_DoesNotWarn()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        File.WriteAllText(Path.Combine(cwd, "staged.cs"), "class Staged { }");
        await RunGit(cwd, "add", "staged.cs");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_1_000000000000000000000", "main");
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", paths: ["staged.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean()); // own lease re-entry is idempotent
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
        Assert.False(doc.RootElement.GetProperty("staged_without_lease").GetBoolean());
    }

    [Fact]
    public async Task ForeignFileLease_OnRequestedPath_ListsConflict_AndStillAcquires()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        await SeedFileLeaseAsync(store, cwd, "ses_test_3_000000000000000000000", "main", ["src/a.cs"], intent: "editing it");
        var stored = Assert.Single(await store.ListAllAsync(), l => l.Kind == CommitLeaseKind.File);
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", paths: ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal("red", doc.RootElement.GetProperty("verdict").GetString());
        var conflicts = doc.RootElement.GetProperty("conflicting_file_leases").EnumerateArray().ToArray();
        var conflict = Assert.Single(conflicts);
        Assert.Equal(stored.LeaseId, conflict.GetProperty("lease_id").GetString());
        Assert.Equal("ses_test_3_000000000000000000000", conflict.GetProperty("holder_caller_id").GetString());
        Assert.Equal(new[] { "src/a.cs" }, conflict.GetProperty("paths").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task ForeignFileLease_WithNoPathsRequested_DoesNotConflict()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        await SeedFileLeaseAsync(store, cwd, "ses_test_3_000000000000000000000", "main", ["src/a.cs"]);
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", paths: []);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
        Assert.False(doc.RootElement.TryGetProperty("conflicting_file_leases", out _));
    }

    [Fact]
    public async Task ForeignFileLease_OnUnrequestedPath_DoesNotConflict()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        await SeedFileLeaseAsync(store, cwd, "ses_test_3_000000000000000000000", "main", ["docs/b.md"]);
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", paths: ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
        Assert.False(doc.RootElement.TryGetProperty("conflicting_file_leases", out _));
    }

    [Fact]
    public async Task ForeignFileLease_DirectoryCoversRequestedPath_ReturnsRed()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        await SeedFileLeaseAsync(store, cwd, "ses_test_3_000000000000000000000", "main", ["src"]);
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", paths: ["src/nested/leaf.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal("red", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Single(doc.RootElement.GetProperty("conflicting_file_leases").EnumerateArray());
    }

    [Fact]
    public async Task RequestedDirectoryCoversForeignFileLease_ReturnsRed()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        await SeedFileLeaseAsync(store, cwd, "ses_test_3_000000000000000000000", "main", ["src/a.cs"]);
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", paths: ["src"]);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal("red", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Single(doc.RootElement.GetProperty("conflicting_file_leases").EnumerateArray());
    }

    [Fact]
    public async Task MixedForeignCommitAndFileLease_DoesNotAcquire_WithHolderAndFileLeases()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "main", intent: "commit slot");
        await SeedFileLeaseAsync(store, cwd, "ses_test_3_000000000000000000000", "main", ["src/a.cs"], intent: "editing");
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", paths: ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal("red", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("ses_test_2_000000000000000000000", doc.RootElement.GetProperty("holder_caller_id").GetString());
        var conflicts = doc.RootElement.GetProperty("conflicting_file_leases").EnumerateArray().ToArray();
        Assert.Equal("ses_test_3_000000000000000000000", Assert.Single(conflicts).GetProperty("holder_caller_id").GetString());
    }

    [Fact]
    public async Task LeaseInOtherWorkingTree_Acquires()
    {
        using var store = NewStore();
        var repo1 = await InitGitRepo("wt-1");
        var repo2 = await InitGitRepo("wt-2");
        var leaseTool = new CommitLeaseTool(store, _clock, repo1);
        await leaseTool.LeaseAsync("ses_test_2_000000000000000000000", "main", intent: "worktree 1 in flight");
        var tool = NewTool(store, repo2);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", paths: ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
        Assert.False(doc.RootElement.TryGetProperty("holder_caller_id", out _));
    }

    [Fact]
    public async Task ThirtyCharCallerId_IsAccepted()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        // caller_id is an arbitrary caller-supplied string (not tied to any
        // host's id format); a 30-character id must clear the length gate.
        var result = await tool.LeaseAsync("ses_1a2b3c4d5e6f7a8b9c0d1e2f3a", "main");

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean());
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Claim_DefaultTtl_Is300()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main");

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean());
        var stored = Assert.Single((await store.ListAllAsync()).Where(l => l.Kind == CommitLeaseKind.Commit));
        Assert.Equal(300, stored.TtlSeconds);
    }

    [Fact]
    public async Task Claim_WithTtl_StoresIt()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", ttl_seconds: 900);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("acquired").GetBoolean());
        var stored = Assert.Single((await store.ListAllAsync()).Where(l => l.Kind == CommitLeaseKind.Commit));
        Assert.Equal(900, stored.TtlSeconds);
    }

    [Fact]
    public async Task Claim_Ttl_ClampedTo3600()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", ttl_seconds: 100000);

        var stored = Assert.Single((await store.ListAllAsync()).Where(l => l.Kind == CommitLeaseKind.Commit));
        Assert.Equal(3600, stored.TtlSeconds);
    }

    [Fact]
    public async Task Claim_Ttl_ClampedTo60()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        await tool.LeaseAsync("ses_test_1_000000000000000000000", "main", ttl_seconds: 1);

        var stored = Assert.Single((await store.ListAllAsync()).Where(l => l.Kind == CommitLeaseKind.Commit));
        Assert.Equal(60, stored.TtlSeconds);
    }

    [Fact]
    public async Task BlankCallerId_ReturnsError_AndStoresNothing()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        var result = await tool.LeaseAsync("   ", "main", paths: ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
        Assert.Empty(await store.ListAllAsync());
    }

    private Task SeedCommitLeaseAsync(
        JsonCommitLeaseStore store,
        string checkout,
        string callerId,
        string branch,
        string? intent = null,
        int ttlSeconds = 300)
        => store.TryLeaseAsync(new CommitLeaseRecord
        {
            LeaseId = Ulid.NewUlid().ToString(),
            CallerId = callerId,
            Kind = CommitLeaseKind.Commit,
            Checkout = checkout,
            Branch = branch,
            Since = _clock.GetUtcNow(),
            LastHeartbeat = _clock.GetUtcNow(),
            TtlSeconds = ttlSeconds,
            Paths = null,
            Intent = intent,
        });

    private Task SeedFileLeaseAsync(
        JsonCommitLeaseStore store,
        string checkout,
        string callerId,
        string branch,
        string[] paths,
        string? intent = null)
        => store.TryLeaseAsync(new CommitLeaseRecord
        {
            LeaseId = Ulid.NewUlid().ToString(),
            CallerId = callerId,
            Kind = CommitLeaseKind.File,
            Checkout = checkout,
            Branch = branch,
            Since = _clock.GetUtcNow(),
            LastHeartbeat = _clock.GetUtcNow(),
            TtlSeconds = 300,
            Paths = paths,
            Intent = intent,
        });

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
