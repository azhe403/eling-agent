using System.ComponentModel;
using Eling.Backend.Bootstrap;
using Eling.Backend.Dtos;
using Eling.Backend.Scope;
using Eling.Backend.Updates;
using Eling.Core;
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

    public MemoryRecallTool(
        IMemoryRecallService recall,
        ILogger<MemoryRecallTool>? logger = null,
        string? cwd = null,
        string? userHomeDirectory = null,
        IProjectScopePolicyStore? policyStore = null,
        IUpdateChecker? updateChecker = null)
    {
        _recall = recall;
        _logger = logger;
        _cwd = cwd;
        _userHomeDirectory = userHomeDirectory;
        _policyStore = policyStore;
        _updateChecker = updateChecker;
    }

    [McpServerTool(Name = "memory_recall"), Description("Call this BEFORE answering or acting, not after. On the first user turn of any session, on every step progression/subtask transition, and whenever the topic shifts, call this first with topics derived from the current step or message — it costs ~500ms and prevents wrong assumptions about project conventions and past decisions. Also triggers on 'eling <topic>'. Returns topic-relevant full memories (search-based recall), the most recently updated active memories (so writes from other agents are visible), outstanding intentions with their trigger-match state, the project-scope posture (posture, policy, adoptable) so the agent can offer onboarding only when adoptable, and lightweight stats.")]
    public async Task<MemoryRecallResponse> RecallAsync(
        [Description("Current task context (filePath, topics, project). Optional.")] MemoryRecallContextInput? context = null,
        [Description("Max recalled memories (default 10).")] int recallLimit = 10,
        [Description("Max recent memories (default 10).")] int recentLimit = 10,
        [Description("project | global | merged (default merged).")] string scope = "merged",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = context is null
                ? null
                : new MemoryRecallContext(context.Topics ?? [], context.FilePath, context.Project);

            var result = await _recall.RecallAsync(ctx, recallLimit, recentLimit, scope, cancellationToken);
            _logger?.LogInformation(
                "memory_recall returned {RecallCount} recalled, {RecentCount} recent, {IntentionCount} intention(s) (scope={Scope})",
                result.RecallMemories.Count, result.RecentMemories.Count, result.Intentions.Count, scope);

            var posture = await ProjectScopePosture.EvaluateAsync(_cwd, _userHomeDirectory, _policyStore);

            var response = new MemoryRecallResponse
            {
                RecallMemories = result.RecallMemories.Select(MemoryRecallMemory.From).ToList().AsReadOnly(),
                RecentMemories = result.RecentMemories.Select(MemoryRecallMemory.From).ToList().AsReadOnly(),
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
