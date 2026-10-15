using System.ComponentModel;
using Eling.Backend.Bootstrap;
using Eling.Backend.Dtos;
using Eling.Backend.Judging;
using Eling.Backend.Scope;
using Eling.Backend.Updates;
using Eling.Core;
using Eling.Core.Memory;
using Eling.Core.MemoryRecall;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// On-demand context hydration. Replaces the previous <c>session_start</c>
/// tool: agents may invoke <c>memory_recall</c> at any point in a
/// conversation to refresh the slice of memory that is relevant to the
/// current task, including memories just written by other agents. It also
/// reports the project-scope posture so the agent knows whether onboarding
/// applies (<c>adoptable</c>) without asking repeatedly.
/// </summary>
[McpServerToolType]
public sealed class MemoryRecallTool
{
    private readonly IMemoryRecallService _recall;
    private readonly ILogger<MemoryRecallTool>? _logger;
    private readonly string? _cwd;
    private readonly string? _userHomeDirectory;
    private readonly IProjectScopePolicyStore? _policyStore;
    private readonly IUpdateChecker? _updateChecker;
    private readonly ISemanticSummarizer? _summarizer;
    private readonly SmartSaveOptions? _smartSave;

    /// <summary>Bound on how many overflow items one summary may cover.</summary>
    private const int MaxSummarizedItems = 10;

    public MemoryRecallTool(
        IMemoryRecallService recall,
        ILogger<MemoryRecallTool>? logger = null,
        string? cwd = null,
        string? userHomeDirectory = null,
        IProjectScopePolicyStore? policyStore = null,
        IUpdateChecker? updateChecker = null,
        ISemanticSummarizer? summarizer = null,
        SmartSaveOptions? smartSave = null)
    {
        _recall = recall;
        _logger = logger;
        _cwd = cwd;
        _userHomeDirectory = userHomeDirectory;
        _policyStore = policyStore;
        _updateChecker = updateChecker;
        _summarizer = summarizer;
        _smartSave = smartSave;
    }

    [McpServerTool(Name = "memory_recall"), Description("Call this BEFORE answering or acting, not after. On the first user turn of any session, on every step progression/subtask transition, and whenever the topic shifts, call this first — with a BROAD topic set: topics derived from the current step or message AND topics covering other relevant areas (standing rules and preferences, standing decisions, lessons and known gotchas, workflow and repo conventions, recently touched work); never anchor solely on the opening message's subject. Pass topics as `context: { topics: [...] }` — a top-level `topics` array is also accepted and, when non-empty, replaces `context.topics`. It costs ~500ms and prevents wrong assumptions about project conventions and past decisions. After a session-start call, check `stats.recallCount`: if it is 0, the call was mis-shaped or matched nothing — re-issue with `context: { topics: [...] }` and broader terms before proceeding (bounded retries). Also triggers on 'eling <topic>'. When topic recall returns more than `recallLimit` items, the response also carries `recallSummary` — digest paragraphs of the overflowing items (the ones the limit would drop); pass `summarizeOverflow: false` to skip it, and read `source` to tell a judge-written digest ('judge') from a deterministic one ('heuristic'). Every response also carries `standingMemories` — a topic-independent baseline of standing rules/preferences, standing decisions, and lessons (project-first, capped); read it on session start even when your topic call was narrow, because it is the server-enforced consistency spine. Returns topic-relevant full memories (search-based recall), the most recently updated active memories (so writes from other agents are visible), the standing baseline, the overflow digest, outstanding intentions with their trigger-match state, the project-scope posture (posture, policy, adoptable) so the agent can offer onboarding only when adoptable, and lightweight stats.")]
    public async Task<MemoryRecallResponse> RecallAsync(
        [Description("Current task context (filePath, topics, project). Optional.")] MemoryRecallContextInput? context = null,
        [Description("Max recalled memories (default 10).")] int recallLimit = 10,
        [Description("Max recent memories (default 10).")] int recentLimit = 10,
        [Description("project | global | merged (default merged).")] string scope = "merged",
        [Description("Alias for context.topics: a top-level topic array. When non-empty, these topics replace context.topics.")] string[]? topics = null,
        [Description("Summarize the recall items beyond recallLimit into recallSummary (default true). Set false to skip for minimum latency.")] bool summarizeOverflow = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = context is null
                ? null
                : new MemoryRecallContext(context.Topics ?? [], context.FilePath, context.Project);

            // Convenience alias: agents intuitively pass a top-level topics
            // array; older builds silently ignored that shape. When present
            // and non-empty, the alias replaces context.topics.
            if (topics is not null)
            {
                var alias = topics.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
                if (alias.Length > 0)
                {
                    ctx = new MemoryRecallContext(alias, context?.FilePath, context?.Project);
                    _logger?.LogInformation(
                        "memory_recall normalized a top-level topics alias ({TopicCount} topic(s)) into context",
                        alias.Length);
                }
            }

            var result = await _recall.RecallAsync(ctx, recallLimit, recentLimit, scope, cancellationToken);
            _logger?.LogInformation(
                "memory_recall returned {RecallCount} recalled, {StandingCount} standing, {OverflowCount} overflow, {RecentCount} recent, {IntentionCount} intention(s) (scope={Scope})",
                result.RecallMemories.Count, result.StandingMemories?.Count ?? 0, result.OverflowMemories?.Count ?? 0, result.RecentMemories.Count, result.Intentions.Count, scope);

            var posture = await ProjectScopePosture.EvaluateAsync(_cwd, _userHomeDirectory, _policyStore);

            var response = new MemoryRecallResponse
            {
                RecallMemories = result.RecallMemories.Select(MemoryRecallMemory.From).ToList().AsReadOnly(),
                RecentMemories = result.RecentMemories.Select(MemoryRecallMemory.From).ToList().AsReadOnly(),
                StandingMemories = result.StandingMemories?.Select(MemoryRecallMemory.From).ToList().AsReadOnly() ?? [],
                Intentions = result.Intentions.Select(MemoryRecallIntention.From).ToList().AsReadOnly(),
                Stats = MemoryRecallStatsDto.From(result.Stats),
                ProjectScope = new MemoryRecallProjectScopeDto
                {
                    Posture = posture.Posture,
                    Policy = posture.Policy,
                    Adoptable = posture.Adoptable,
                    Initialized = posture.Initialized,
                    HeadRoot = posture.HeadRoot
                }
            };

            response.RecallSummary = await BuildRecallSummaryAsync(result.OverflowMemories, summarizeOverflow, cancellationToken);

            await AttachUpdateNoticeAsync(response, cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "memory_recall threw an exception (scope={Scope}, recallLimit={RecallLimit}, recentLimit={RecentLimit})", scope, recallLimit, recentLimit);
            throw;
        }
    }

    /// <summary>
    /// Summarizes the overflow hits — the relevant memories beyond
    /// <c>recallLimit</c> that the limit would otherwise drop. The semantic
    /// summarizer writes the digest when available; every failure degrades to
    /// the deterministic digest and is recorded in
    /// <see cref="RecallSummaryDto.Note"/>, mirroring the save pipeline's
    /// judge-failed posture so a dead provider is diagnosable, not silent.
    /// </summary>
    private async Task<RecallSummaryDto?> BuildRecallSummaryAsync(
        IReadOnlyList<MemoryRecallHit>? overflow,
        bool summarizeOverflow,
        CancellationToken cancellationToken)
    {
        if (!summarizeOverflow || overflow is null || overflow.Count == 0)
        {
            return null;
        }

        var items = overflow.Take(MaxSummarizedItems).Select(h => h.Memory).ToList();
        var summary = new RecallSummaryDto
        {
            ItemCount = overflow.Count,
            SummarizedCount = items.Count,
            Source = "heuristic",
            Paragraphs = RecallSummaryFallback.Build(items)
        };

        if (_summarizer is null)
        {
            summary.Note = "judge not configured; heuristic digest";
            return summary;
        }

        var budget = (_smartSave ?? new SmartSaveOptions()).JudgeTimeout;
        try
        {
            using var timeout = new CancellationTokenSource(budget);
            var digest = await _summarizer.SummarizeAsync(items, timeout.Token);
            if (digest.Paragraphs.Count > 0)
            {
                summary.Source = digest.Source;
                summary.Paragraphs = digest.Paragraphs;
            }
            else
            {
                summary.Note = "judge returned no usable summary; heuristic digest";
            }
        }
        catch (OperationCanceledException)
        {
            summary.Note = $"judge timed out after {budget.TotalSeconds:0}s; heuristic digest";
        }
        catch (Exception ex)
        {
            // Transport, provider, and parsing faults land here. Record only the
            // exception type: provider errors can carry header or key material.
            summary.Note = $"judge-failed: {ex.GetType().Name}; heuristic digest";
        }

        return summary;
    }

    /// <summary>
    /// Attaches the update notice when one is available. Isolated in its own
    /// try block so a checker failure can never break recall itself.
    /// </summary>
    private async Task AttachUpdateNoticeAsync(MemoryRecallResponse response, CancellationToken cancellationToken)
    {
        if (_updateChecker is null)
        {
            return;
        }

        try
        {
            response.Update = UpdateNoticeDto.FromStatus(await _updateChecker.GetStatusAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to attach the update notice to memory_recall");
        }
    }
}
