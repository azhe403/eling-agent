using System.Diagnostics;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Eling.Backend.Mcp.Tools;
using Eling.Core.CommitLease;

namespace Eling.Backend.Tests;

public sealed class CommitCheckConflictToolTests : IDisposable
{
    private readonly string _root;
    private readonly ManualTimeProvider _clock;

    public CommitCheckConflictToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-commit-check-conflict-tool-" + Guid.NewGuid().ToString("N")[..8]);
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

    private JsonCommitLeaseStore NewStore()
        => new(Path.Combine(_root, "commit-leases.json"), notifier: null, timeProvider: _clock);

    private CommitCheckConflictTool NewTool(JsonCommitLeaseStore store, string cwd)
        => new(store, _clock, cwd);

    private Task SeedCommitLeaseAsync(
        JsonCommitLeaseStore store,
        string checkout,
        string sessionId,
        string branch,
        string? intent = null,
        string[]? paths = null,
        int ttlSeconds = 300)
        => store.TryLeaseAsync(new CommitLeaseRecord {
            LeaseId =  Ulid.NewUlid().ToString(),
            SessionId =  sessionId,
            Kind =  CommitLeaseKind.Commit,
            Checkout =  checkout,
            Branch =  branch,
            Since =  _clock.GetUtcNow(),
            LastHeartbeat =  _clock.GetUtcNow(),
            TtlSeconds =  ttlSeconds,
            Paths =  paths,
            Intent =  intent});

    private Task SeedFileLeaseAsync(
        JsonCommitLeaseStore store,
        string checkout,
        string sessionId,
        string branch,
        string[] paths,
        string? intent = null)
        => store.TryLeaseAsync(new CommitLeaseRecord {
            LeaseId =  Ulid.NewUlid().ToString(),
            SessionId =  sessionId,
            Kind =  CommitLeaseKind.File,
            Checkout =  checkout,
            Branch =  branch,
            Since =  _clock.GetUtcNow(),
            LastHeartbeat =  _clock.GetUtcNow(),
            TtlSeconds =  300,
            Paths =  paths,
            Intent =  intent});

    [Fact]
    public async Task CleanTree_NoLeases_ReturnsGreen()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        var tool = NewTool(store, cwd);

        var result = await tool.CheckConflictAsync("ses_test_1_000000000000000000000", ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
        Assert.False(doc.RootElement.GetProperty("staged_without_lease").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("holder", out _));
        Assert.False(doc.RootElement.TryGetProperty("conflicting_file_leases", out _));
    }

    [Fact]
    public async Task ForeignCommitLease_SameCheckoutAndBranch_ReturnsRedWithHolderInfo()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "main", intent: "land the feature");
        _clock.Advance(TimeSpan.FromSeconds(45));
        var tool = NewTool(store, cwd);

        var result = await tool.CheckConflictAsync("ses_test_1_000000000000000000000", ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("red", doc.RootElement.GetProperty("verdict").GetString());
        var holder = doc.RootElement.GetProperty("holder");
        Assert.Equal("ses_test_2_000000000000000000000", holder.GetProperty("holder_session_id").GetString());
        Assert.Equal(45, holder.GetProperty("lease_age_seconds").GetInt64());
        Assert.Equal("land the feature", holder.GetProperty("intent").GetString());
        Assert.False(doc.RootElement.GetProperty("staged_without_lease").GetBoolean());
    }

    [Fact]
    public async Task ForeignCommitLease_OnOtherBranch_ReturnsGreen()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a", branch: "main");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "develop", intent: "other branch work");
        var tool = NewTool(store, cwd);

        var result = await tool.CheckConflictAsync("ses_test_1_000000000000000000000", ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
        Assert.False(doc.RootElement.TryGetProperty("holder", out _));
    }

    [Fact]
    public async Task NonGitDirectory_ForeignCommitLease_StillDetectedOnAnyBranch()
    {
        using var store = NewStore();
        var cwd = Checkout("a"); // no git init: branch is undiscoverable, so the fail-safe scope is any branch of this checkout
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "main", intent: "holds the slot");
        var tool = NewTool(store, cwd);

        var result = await tool.CheckConflictAsync("ses_test_1_000000000000000000000", ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("red", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("ses_test_2_000000000000000000000", doc.RootElement.GetProperty("holder").GetProperty("holder_session_id").GetString());
    }

    [Fact]
    public async Task StagedFiles_CallerHoldsNoLease_VerdictGreen_AndWarns()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        File.WriteAllText(Path.Combine(cwd, "staged.cs"), "class Staged { }");
        await RunGit(cwd, "add", "staged.cs");
        var tool = NewTool(store, cwd);

        var result = await tool.CheckConflictAsync("ses_test_1_000000000000000000000", ["staged.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
        Assert.True(doc.RootElement.GetProperty("staged_without_lease").GetBoolean());
    }

    [Fact]
    public async Task StagedFiles_CallerHoldsOwnLease_DoesNotWarn()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        File.WriteAllText(Path.Combine(cwd, "staged.cs"), "class Staged { }");
        await RunGit(cwd, "add", "staged.cs");
        var leaseTool = new CommitLeaseTool(store, _clock, cwd);
        await leaseTool.LeaseAsync("ses_test_1_000000000000000000000", "main");
        var tool = NewTool(store, cwd);

        var result = await tool.CheckConflictAsync("ses_test_1_000000000000000000000", ["staged.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
        Assert.False(doc.RootElement.GetProperty("staged_without_lease").GetBoolean());
    }

    [Fact]
    public async Task ForeignFileLease_OnRequestedPath_ReturnsRedAndListsLease()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        await SeedFileLeaseAsync(store, cwd, "ses_test_3_000000000000000000000", "main", ["src/a.cs"], intent: "editing it");
        var stored = Assert.Single(await store.ListAllAsync());
        var tool = NewTool(store, cwd);

        var result = await tool.CheckConflictAsync("ses_test_1_000000000000000000000", ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("red", doc.RootElement.GetProperty("verdict").GetString());
        Assert.False(doc.RootElement.TryGetProperty("holder", out _));
        var conflicts = doc.RootElement.GetProperty("conflicting_file_leases").EnumerateArray().ToArray();
        var conflict = Assert.Single(conflicts);
        Assert.Equal(stored.LeaseId, conflict.GetProperty("lease_id").GetString());
        Assert.Equal("ses_test_3_000000000000000000000", conflict.GetProperty("holder_session_id").GetString());
        Assert.Equal(new[] { "src/a.cs" }, conflict.GetProperty("paths").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task ForeignFileLease_WithNoPathsRequested_DoesNotConflict()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        await SeedFileLeaseAsync(store, cwd, "ses_test_3_000000000000000000000", "main", ["src/a.cs"]);
        var tool = NewTool(store, cwd);

        var result = await tool.CheckConflictAsync("ses_test_1_000000000000000000000", []);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
        Assert.False(doc.RootElement.TryGetProperty("conflicting_file_leases", out _));
    }

    [Fact]
    public async Task OwnFileLease_OnRequestedPath_IsNotAConflict()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        await SeedFileLeaseAsync(store, cwd, "ses_test_1_000000000000000000000", "main", ["src/a.cs"]);
        var tool = NewTool(store, cwd);

        var result = await tool.CheckConflictAsync("ses_test_1_000000000000000000000", ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
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

        var result = await tool.CheckConflictAsync("ses_test_1_000000000000000000000", ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
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

        var result = await tool.CheckConflictAsync("ses_test_1_000000000000000000000", ["src/nested/leaf.cs"]);

        using var doc = JsonDocument.Parse(result);
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

        var result = await tool.CheckConflictAsync("ses_test_1_000000000000000000000", ["src"]);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("red", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Single(doc.RootElement.GetProperty("conflicting_file_leases").EnumerateArray());
    }

    [Fact]
    public async Task MixedForeignCommitAndFileLease_ReturnsRed_WithHolderAndFileLeases()
    {
        using var store = NewStore();
        var cwd = await InitGitRepo("a");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "main", intent: "commit slot");
        await SeedFileLeaseAsync(store, cwd, "ses_test_3_000000000000000000000", "main", ["src/a.cs"], intent: "editing");
        var tool = NewTool(store, cwd);

        var result = await tool.CheckConflictAsync("ses_test_1_000000000000000000000", ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("red", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("ses_test_2_000000000000000000000", doc.RootElement.GetProperty("holder").GetProperty("holder_session_id").GetString());
        var conflicts = doc.RootElement.GetProperty("conflicting_file_leases").EnumerateArray().ToArray();
        Assert.Equal("ses_test_3_000000000000000000000", Assert.Single(conflicts).GetProperty("holder_session_id").GetString());
    }

    [Fact]
    public async Task LeaseInOtherWorkingTree_DoesNotConflict()
    {
        // Leases key by the working-copy root: a linked worktree is its own
        // checkout, so this test uses two independent repos to prove a lease
        // taken in one working tree does not conflict in another.
        using var store = NewStore();
        var repo1 = await InitGitRepo("wt-1");
        var repo2 = await InitGitRepo("wt-2");
        var leaseTool = new CommitLeaseTool(store, _clock, repo1);
        await leaseTool.LeaseAsync("ses_test_2_000000000000000000000", "main", intent: "worktree 1 in flight");
        var tool = NewTool(store, repo2);

        var result = await tool.CheckConflictAsync("ses_test_1_000000000000000000000", ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
        Assert.False(doc.RootElement.TryGetProperty("holder", out _));
    }

    [Fact]
    public async Task BlankSessionId_ReturnsError_AndStoresNothing()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        var result = await tool.CheckConflictAsync("   ", ["src/a.cs"]);

        using var doc = JsonDocument.Parse(result);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
        Assert.Empty(await store.ListAllAsync());
    }

    [Fact]
    public void PorcelainParser_CleanOutput_HasNoEntries()
    {
        var state = CommitCheckConflictTool.GitStatusReader.ParsePorcelain("");

        Assert.False(state.HasStagedChanges);
        Assert.Empty(state.StagedPaths);
    }

    [Fact]
    public void PorcelainParser_StagedModification_IsDetected()
    {
        var state = CommitCheckConflictTool.GitStatusReader.ParsePorcelain("M  src/a.cs\n");

        Assert.True(state.HasStagedChanges);
        Assert.Equal(new[] { "src/a.cs" }, state.StagedPaths);
    }

    [Fact]
    public void PorcelainParser_UnstagedAndUntracked_AreNotStaged()
    {
        var state = CommitCheckConflictTool.GitStatusReader.ParsePorcelain(" M src/a.cs\n?? src/new.cs\n");

        Assert.False(state.HasStagedChanges);
        Assert.Empty(state.StagedPaths);
    }

    [Fact]
    public void PorcelainParser_RenamedEntry_UsesNewPath()
    {
        var state = CommitCheckConflictTool.GitStatusReader.ParsePorcelain("R  old/name.cs -> new/name.cs\n");

        Assert.True(state.HasStagedChanges);
        Assert.Equal(new[] { "new/name.cs" }, state.StagedPaths);
    }

    [Fact]
    public void PorcelainParser_QuotedPath_IsUnquoted()
    {
        var state = CommitCheckConflictTool.GitStatusReader.ParsePorcelain("A  \"src/quo ted.cs\"\n");

        Assert.True(state.HasStagedChanges);
        Assert.Equal(new[] { "src/quo ted.cs" }, state.StagedPaths);
    }

    [Fact]
    public void PorcelainParser_CrLfAndBlankLines_AreTolerated()
    {
        var state = CommitCheckConflictTool.GitStatusReader.ParsePorcelain("M  src/a.cs\r\n\r\nA  src/b.cs\r\n");

        Assert.True(state.HasStagedChanges);
        Assert.Equal(new[] { "src/a.cs", "src/b.cs" }, state.StagedPaths);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
