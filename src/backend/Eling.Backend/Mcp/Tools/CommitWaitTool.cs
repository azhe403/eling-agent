using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Core.Git;
using Eling.Core.CommitLease;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Server-side blocking wait for the commit slot on the current checkout and
/// branch. Polls the lease store until no live commit lease owned by a
/// different session exists (verdict green) or the total timeout elapses
/// (verdict red, with the holder's info). Does not take the lease: the caller
/// must still call <c>commit_lease</c> after a green verdict.
/// </summary>
/// <remarks>
/// Checkout is the working-copy root resolved via
/// <see cref="GitRepositoryResolver.ResolveWorkingCopyRoot"/> from the injected
/// working directory (default <see cref="Directory.GetCurrentDirectory"/>), so
/// leases taken from any subdirectory of a working copy key to that
/// working copy's root. A linked worktree keys by the worktree path,
/// distinct from the main checkout. A path outside every git repository
/// falls back to the working directory itself, keyed by that directory.
/// <para>
/// The branch is resolved from the optional <c>branch</c> argument, falling
/// back to discovery via <c>git branch --show-current</c>. When neither is
/// available (git missing, detached HEAD, or a non-git directory) the fail-safe
/// scope is every branch of the checkout: a foreign live commit lease on any
/// branch keeps the wait blocking.
/// </para>
/// <para>
/// Polling uses exponential backoff from
/// <paramref name="initial_interval_seconds"/>, doubling each poll and capped
/// at 20 seconds (the first poll included). The store is only read, never
/// mutated, and no store gate is held across the delay. Caller cancellation
/// propagates.
/// </para>
/// <para>
/// Green means the store reported no foreign live lease at poll time. The
/// store's read path treats I/O and lock failures as "no leases", so under a
/// broken store a green may result from a failed read rather than a genuinely
/// free slot; callers should not treat green as a hard guarantee when the
/// store itself is unhealthy.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class CommitWaitTool
{
    /// <summary>Upper bound for the backoff between polls.</summary>
    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(20);

    private readonly ICommitLeaseStore _leaseStore;
    private readonly TimeProvider _timeProvider;
    private readonly string _cwd;
    private readonly ILogger<CommitWaitTool>? _logger;

    public CommitWaitTool(
        ICommitLeaseStore leaseStore,
        TimeProvider? timeProvider = null,
        string? cwd = null,
        ILogger<CommitWaitTool>? logger = null)
    {
        _leaseStore = leaseStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cwd = Path.GetFullPath(string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd);
        _logger = logger;
    }

    // Parameter names below use snake_case deliberately: the MCP host passes
    // C# parameter names through to the wire schema unchanged, and the
    // commit_* tool surface is snake_case end to end.
    [McpServerTool(Name = "commit_wait"), Description("Wait server-side until no other session holds a live commit lease on this checkout and branch, then return green. Returns { verdict: 'green' | 'red', waited_seconds, holder?: {...} }. Green ends the wait without taking the lease - call commit_lease next. Red means the timeout elapsed while another session still holds the slot: stop and report the holder. branch is optional and is discovered via git when omitted.")]
    public async Task<string> WaitAsync(
        [Description("The calling session's id. Must be at least 32 characters.")] string session_id,
        [Description("Give up after this many seconds and return red. Default 180.")] double total_timeout_seconds = 180,
        [Description("First poll interval in seconds; doubles each poll, capped at 20 (values above 20 are clamped to 20). Default 5.")] double initial_interval_seconds = 5,
        [Description("Current branch of the checkout. Optional; discovered via git when omitted. Pass it to skip discovery (e.g. when git is unavailable).")] string? branch = null,
        CancellationToken cancellationToken = default)
    {
        if (!CommitLeaseToolHelpers.IsSessionIdPlausible(session_id))
        {
            return CommitLeaseToolHelpers.Serialize(new CommitWaitError(CommitLeaseToolHelpers.SessionIdTooShortMessage(session_id)));
        }

        if (!double.IsFinite(total_timeout_seconds) || total_timeout_seconds <= 0)
        {
            return CommitLeaseToolHelpers.Serialize(new CommitWaitError("total_timeout_seconds must be a finite number greater than 0."));
        }

        if (!double.IsFinite(initial_interval_seconds) || initial_interval_seconds <= 0)
        {
            return CommitLeaseToolHelpers.Serialize(new CommitWaitError("initial_interval_seconds must be a finite number greater than 0."));
        }

        try
        {
            var caller = session_id.Trim();
            var checkout = GitRepositoryResolver.ResolveWorkingCopyRoot(_cwd) ?? _cwd;
            var branchKey = string.IsNullOrWhiteSpace(branch)
                ? (await CommitCheckConflictTool.GitStatusReader.TryRead(checkout, _logger, cancellationToken))?.CurrentBranch
                : branch.Trim();
            branchKey = string.IsNullOrWhiteSpace(branchKey) ? null : branchKey.Trim();

            // Wall-clock, not the injected TimeProvider: the wait limit and the
            // reported waited_seconds are about real elapsed time, while the
            // TimeProvider keeps freshness semantics of lease ages deterministic.
            var started = Stopwatch.GetTimestamp();
            var timeout = TimeSpan.FromSeconds(total_timeout_seconds);
            var delay = TimeSpan.FromSeconds(initial_interval_seconds);
            if (delay > MaxPollInterval) delay = MaxPollInterval;

            while (true)
            {
                // One read per poll serves the whole decision; the store sweeps
                // stale leases as part of ListActiveAsync. No gate is held across
                // the Task.Delay below.
                var activeLeases = await _leaseStore.ListActiveAsync(checkout, null, cancellationToken);
                var foreignCommitLeases = activeLeases
                    .Where(l => l.Kind == CommitLeaseKind.Commit
                        && !string.Equals(l.SessionId, caller, StringComparison.Ordinal)
                        && CommitLeaseToolHelpers.BranchMatches(l.Branch, branchKey))
                    .OrderBy(l => l.Since)
                    .ToList();

                var elapsed = Stopwatch.GetElapsedTime(started);
                if (foreignCommitLeases.Count == 0)
                {
                    return CommitLeaseToolHelpers.Serialize(new CommitWaitResult
                    {
                        Verdict = "green",
                        WaitedSeconds = WaitedSeconds(elapsed),
                        Holder = null,
                    });
                }

                if (elapsed >= timeout)
                {
                    return CommitLeaseToolHelpers.Serialize(new CommitWaitResult
                    {
                        Verdict = "red",
                        WaitedSeconds = WaitedSeconds(elapsed),
                        Holder = HolderInfoOf(foreignCommitLeases[0], _timeProvider.GetUtcNow()),
                    });
                }

                var remaining = timeout - elapsed;
                var sleep = delay < remaining ? delay : remaining;
                await Task.Delay(sleep, cancellationToken);
                delay = delay + delay > MaxPollInterval ? MaxPollInterval : delay + delay;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled: propagate so the turn aborts normally.
            _logger?.LogDebug("commit_wait was cancelled by the caller (session {SessionId}).", session_id);
            throw;
        }
        catch (Exception ex)
        {
            // The store's gate wait and its file I/O both throw; the caller gets
            // the repo's compact error shape, not an opaque host-level failure.
            _logger?.LogWarning(ex, "commit_wait failed for session {SessionId}.", session_id);
            return CommitLeaseToolHelpers.Serialize(new CommitWaitError($"Could not wait for the commit lease: {ex.Message}"));
        }
    }

    private static double WaitedSeconds(TimeSpan elapsed)
        => Math.Round(elapsed.TotalSeconds, 3);

    private static HolderInfo HolderInfoOf(CommitLeaseRecord holder, DateTimeOffset now)
        => new()
        {
            HolderSessionId = holder.SessionId,
            Since = holder.Since,
            LeaseAgeSeconds = CommitLeaseToolHelpers.AgeSeconds(now, holder.Since),
            Intent = CommitLeaseToolHelpers.NormalizeIntent(holder.Intent),
        };

    private sealed record CommitWaitResult
    {
        [JsonPropertyName("verdict")]
        public required string Verdict { get; init; }

        [JsonPropertyName("waited_seconds")]
        public required double WaitedSeconds { get; init; }

        [JsonPropertyName("holder")]
        public required HolderInfo? Holder { get; init; }
    }

    private sealed record CommitWaitError(
        [property: JsonPropertyName("error")]
        string Error);
}
