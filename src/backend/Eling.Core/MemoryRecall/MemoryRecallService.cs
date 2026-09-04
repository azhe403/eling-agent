namespace Eling.Core;

/// <summary>
/// Application-layer service that bundles topic-based recall, recently
/// updated active memories, and outstanding intentions into a single payload
/// for the <c>memory_recall</c> MCP tool. Replaces the previous
/// <c>session_start</c> implementation, which discarded the topics parameter
/// and only ever returned recent memories. This implementation actually
/// performs a search across <see cref="IScopedMemoryService"/> using the
/// joined topics as the query, so recall is no longer a no-op.
/// </summary>
public sealed class MemoryRecallService : IMemoryRecallService
{
    private readonly IScopedMemoryService _scoped;
    private readonly IIntentionStorage _intentions;

    public MemoryRecallService(IScopedMemoryService scoped, IIntentionStorage intentions)
    {
        _scoped = scoped;
        _intentions = intentions;
    }

    public async Task<MemoryRecallResult> RecallAsync(
        MemoryRecallContext? context,
        int recallLimit = 10,
        int recentLimit = 10,
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recallLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(recentLimit);

        var now = DateTimeOffset.UtcNow;

        // Intentions: enumerate once, classify each relative to the supplied
        // context. This part is unchanged from the previous session_start
        // behaviour: intention matching is the only thing that actually worked
        // there, and it remains useful for the on-demand recall use case.
        var allIntentions = await _intentions.ListAllAsync();
        var intentionResults = new List<MemoryRecallIntentionResult>(allIntentions.Count);
        var triggered = 0;
        var expired = 0;
        foreach (var intention in allIntentions)
        {
            if (!IntentionTriggerMatcher.IsOutstanding(intention, now))
            {
                if (IntentionTriggerMatcher.IsExpired(intention, now)) expired++;
                continue;
            }
            var (matched, isExpired) = IntentionTriggerMatcher.Match(intention, context, now);
            if (isExpired) expired++;
            if (matched) triggered++;
            intentionResults.Add(new MemoryRecallIntentionResult(intention, matched, isExpired));
        }

        // Topic-based recall: join non-empty topics into a single FTS query
        // and resolve the ranked IDs back to full Memory payloads. We rely
        // on SearchAsync's ranking (SQLite FTS5 BM25) to order results, so
        // no client-side re-ranking is needed.
        var recall = new List<Memory>(recallLimit);
        var topics = context?.Topics ?? [];
        if (topics.Count > 0 && recallLimit > 0)
        {
            var query = string.Join(' ', topics.Where(t => !string.IsNullOrWhiteSpace(t)));
            if (!string.IsNullOrWhiteSpace(query))
            {
                var hits = await _scoped.SearchAsync(query, scope, recallLimit);
                // De-duplicate by memory id; search may return the same id
                // across project/global when scope = "merged".
                var seen = new HashSet<MemoryId>();
                foreach (var hit in hits)
                {
                    if (!seen.Add(hit.Id)) continue;
                    var reference = hit.Scope == MemoryScopeKind.Global
                        ? MemoryReference.ForGlobal(hit.Id)
                        : MemoryReference.ForProject(hit.Id, hit.ProjectRoot ?? _scoped.ProjectRoot ?? ".");
                    var scoped = await _scoped.GetByIdAsync(reference);
                    if (scoped is not null) recall.Add(scoped.Memory);
                    if (recall.Count >= recallLimit) break;
                }
            }
        }

        // Recent: most recently updated active memories in the requested
        // scope. The previous session_start hard-coded Take(5) and ignored
        // its recentLimit parameter; we honour it here.
        var recent = new List<Memory>(recentLimit);
        if (recentLimit > 0)
        {
            var active = await _scoped.ListAsync(scope, MemoryStatus.Active);
            recent.AddRange(active
                .OrderByDescending(s => s.Memory.UpdatedAt)
                .Take(recentLimit)
                .Select(s => s.Memory));
        }

        // Stats: total + active counts derive from ListAsync rather than two
        // hard-coded project/global calls, so they reflect whatever scope
        // the caller asked for.
        var allMemories = await _scoped.ListAsync(scope, null);
        var activeMemories = await _scoped.ListAsync(scope, MemoryStatus.Active);
        var stats = new MemoryRecallStats(
            TotalMemories: allMemories.Count,
            ActiveMemories: activeMemories.Count,
            ActiveIntentions: intentionResults.Count,
            RecallCount: recall.Count,
            RecentCount: recent.Count);

        return new MemoryRecallResult(recall, recent, intentionResults, stats);
    }
}
