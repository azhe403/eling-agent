using Eling.Core.Memory;
using Eling.Core.Memory.Serialization;
using Microsoft.Extensions.Logging;

namespace Eling.Core.MemoryRecall;

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
    private readonly ILogger<MemoryRecallService>? _logger;

    public MemoryRecallService(
        IScopedMemoryService scoped,
        IIntentionStorage intentions,
        ILogger<MemoryRecallService>? logger = null)
    {
        _scoped = scoped;
        _intentions = intentions;
        _logger = logger;
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
        var intentionResults = allIntentions.Count == 0
            ? (IReadOnlyList<MemoryRecallIntentionResult>)[]
            : new List<MemoryRecallIntentionResult>(allIntentions.Count);
        var triggered = 0;
        var expired = 0;
        if (allIntentions.Count > 0)
        {
            var intentionList = (List<MemoryRecallIntentionResult>)intentionResults;
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
                intentionList.Add(new MemoryRecallIntentionResult(intention, matched, isExpired));
            }
        }

        // Topic-based recall: join non-empty topics into a single FTS query
        // and resolve the ranked IDs back to full Memory payloads. We rely
        // on SearchAsync's ranking (SQLite FTS5 BM25) to order results, so
        // no client-side re-ranking is needed.
        var recall = new List<MemoryRecallHit>(recallLimit);
        var overflow = new List<MemoryRecallHit>(OverflowWindow);
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
                    var reference = hit.Scope switch
                    {
                        MemoryScopeKind.Global => MemoryReference.ForGlobal(hit.Id),
                        MemoryScopeKind.ProjectLocal => MemoryReference.ForProjectLocal(
                            hit.Id, hit.ProjectRoot ?? _scoped.CanonicalRoot ?? _scoped.ProjectRoot ?? "."),
                        _ => MemoryReference.ForProject(hit.Id, hit.ProjectRoot ?? _scoped.ProjectRoot ?? "."),
                    };
                    var scoped = await _scoped.GetByIdAsync(reference);
                    if (hit.QueryMode == "or-fallback")
                    {
                        _logger?.LogDebug(
                            "memory_recall fell back to OR queries: tokens={TokenCount}, scope={Scope}",
                            topics.Count, scope);
                    }
                    if (scoped is not null)
                    {
                        recall.Add(new MemoryRecallHit(
                            scoped.Memory,
                            hit.MatchedVia,
                            hit.PorterScore,
                            hit.TrigramScore,
                            hit.QueryMode,
                            scoped.Scope,
                            scoped.ProjectRoot));
                    }
                    if (recall.Count >= recallLimit) break;
                }

                // Overflow can only exist once the cap is filled, so the wider
                // second fetch runs at most once and only then. Requesting the
                // window from the start would weaken the interleave guarantee
                // the primary list depends on (a level with hits is guaranteed
                // a slot within the first N).
                if (recall.Count >= recallLimit)
                {
                    var known = recall.Select(h => h.Memory.Id).ToHashSet();
                    var wideHits = await _scoped.SearchAsync(query, scope, recallLimit + OverflowWindow);
                    foreach (var hit in wideHits)
                    {
                        if (overflow.Count >= OverflowWindow) break;
                        if (!known.Add(hit.Id)) continue;
                        var reference = hit.Scope switch
                        {
                            MemoryScopeKind.Global => MemoryReference.ForGlobal(hit.Id),
                            MemoryScopeKind.ProjectLocal => MemoryReference.ForProjectLocal(
                                hit.Id, hit.ProjectRoot ?? _scoped.CanonicalRoot ?? _scoped.ProjectRoot ?? "."),
                            _ => MemoryReference.ForProject(hit.Id, hit.ProjectRoot ?? _scoped.ProjectRoot ?? "."),
                        };
                        var scoped = await _scoped.GetByIdAsync(reference);
                        if (scoped is not null)
                        {
                            overflow.Add(new MemoryRecallHit(
                                scoped.Memory,
                                hit.MatchedVia,
                                hit.PorterScore,
                                hit.TrigramScore,
                                hit.QueryMode,
                                scoped.Scope,
                                scoped.ProjectRoot));
                        }
                    }
                }
            }
        }

        // Active enumeration is needed three ways — recent, the standing
        // baseline, and the active stat — so fetch it once.
        var activeMemories = await _scoped.ListAsync(scope, MemoryStatus.Active);
        var currentRoot = _scoped.ProjectRoot;

        // Recent: most recently updated active memories in the requested
        // scope, keeping their scope provenance. The previous session_start
        // hard-coded Take(5) and ignored its recentLimit parameter; we honour
        // it here.
        //
        // Recency alone ranks unrelated work above local context: global
        // memories written by any other project are always "recent" and will
        // crowd out the current project's own conventions. Order so the
        // current project root wins ties, and only fall back to other roots
        // (ancestors, global) once the local ones run out.
        var recent = new List<ScopedMemory>(recentLimit);
        if (recentLimit > 0)
        {
            recent.AddRange(activeMemories
                .OrderBy(s => IsCurrentProject(s, currentRoot) ? 0 : 1)
                .ThenByDescending(s => s.Memory.UpdatedAt)
                .Take(recentLimit));
        }

        // Standing baseline: the topic-independent consistency spine, so even
        // a narrow topic call (or one that matched nothing) still surfaces
        // the standing rules, decisions, and lessons the conversation must
        // not contradict.
        var standing = BuildStandingBaseline(activeMemories, currentRoot, recall);

        // Stats: total + active counts derive from ListAsync rather than two
        // hard-coded project/global calls, so they reflect whatever scope
        // the caller asked for.
        var allMemories = await _scoped.ListAsync(scope, null);
        var stats = new MemoryRecallStats(
            TotalMemories: allMemories.Count,
            ActiveMemories: activeMemories.Count,
            ActiveIntentions: intentionResults.Count,
            RecallCount: recall.Count,
            RecentCount: recent.Count);

        return new MemoryRecallResult(recall, recent, intentionResults, stats, standing, overflow.Count > 0 ? overflow : null);
    }

    /// <summary>Cap on the standing baseline so a broad payload never swamps the topic hits.</summary>
    private const int StandingBaselineLimit = 5;

    /// <summary>
    /// How many relevant hits beyond <c>recallLimit</c> to fetch for the
    /// overflow summary. Keeps the summary input bounded while still
    /// capturing the items the limit would have dropped.
    /// </summary>
    private const int OverflowWindow = 10;

    /// <summary>Memory kinds that define how work must be done, independent of any topic.</summary>
    private static readonly MemoryType[] StandingTypes =
    [
        MemoryType.Preference, MemoryType.Decision, MemoryType.Lesson
    ];

    /// <summary>
    /// Builds the topic-independent baseline: standing rules/preferences,
    /// standing decisions, and lessons, ordered current-project first then
    /// most recently updated, capped at <see cref="StandingBaselineLimit"/>.
    /// Ids already present in the topic hits are skipped so the same memory
    /// never shows up twice in the two primary sections.
    /// </summary>
    private static IReadOnlyList<MemoryRecallHit> BuildStandingBaseline(
        IReadOnlyCollection<ScopedMemory> active,
        string? currentRoot,
        IReadOnlyCollection<MemoryRecallHit> topicHits)
    {
        var alreadySurfaced = topicHits.Select(h => h.Memory.Id).ToHashSet();
        return active
            .Where(s => StandingTypes.Contains(s.Memory.Type) && !alreadySurfaced.Contains(s.Memory.Id))
            .OrderBy(s => IsCurrentProject(s, currentRoot) ? 0 : 1)
            .ThenByDescending(s => s.Memory.UpdatedAt)
            .Take(StandingBaselineLimit)
            .Select(s => new MemoryRecallHit(
                s.Memory,
                ["standing"],
                0.0,
                0.0,
                "standing-baseline",
                s.Scope,
                s.ProjectRoot))
            .ToList();
    }

    /// <summary>
    /// True when the memory lives in the project the caller is currently
    /// working in, either directly or at an ancestor root. Global memories
    /// (null <see cref="ScopedMemory.ProjectRoot"/>) are not current-project:
    /// they are cross-project knowledge and rank below anything local.
    /// </summary>
    private static bool IsCurrentProject(ScopedMemory memory, string? currentRoot)
    {
        if (memory.ProjectRoot is null || currentRoot is null) return false;
        return PathsEqual(memory.ProjectRoot, currentRoot);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
