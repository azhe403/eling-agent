using Eling.Core.Memory.Storage;
using Microsoft.Extensions.Logging;

namespace Eling.Core.Memory;

public class MemoryService : IMemoryService
{
    private readonly IMemoryStorage _storage;
    private readonly IMemoryIndex _index;
    private readonly SmartSaveOptions _smartSave;
    private readonly ISemanticJudge? _judge;
    private readonly ILogger<MemoryService>? _logger;

    public MemoryService(IMemoryStorage storage, IMemoryIndex index)
        : this(storage, index, new SmartSaveOptions())
    {
    }

    public MemoryService(
        IMemoryStorage storage,
        IMemoryIndex index,
        SmartSaveOptions smartSave,
        ISemanticJudge? judge = null,
        ILogger<MemoryService>? logger = null)
    {
        _storage = storage;
        _index = index;
        _smartSave = smartSave;
        _judge = judge;
        _logger = logger;
    }

    public async Task<MemorySaveResult> SaveAsync(Memory memory)
    {
        var retrieval = await RetrieveAsync(memory);

        // Identical content is the one case the heuristic cannot get wrong: a memory
        // cannot contradict itself, so it is merged without spending a judge call.
        if (retrieval.IsExactMatch && retrieval.BestMatch is not null)
        {
            var exact = await MergeIntoAsync(retrieval.BestMatch, memory);
            return new MemorySaveResult(exact, SaveAction.Updated, retrieval.BestMatch, retrieval.NearMatches, retrieval.Reason, retrieval.MatchScore);
        }

        // A judge, when configured, is the decider: the heuristic only narrowed the
        // field. Without one the historical heuristic behaviour is kept verbatim.
        if (_judge is not null)
        {
            return await SaveJudgedAsync(memory, retrieval);
        }

        if (retrieval.BestMatch is not null)
        {
            var merged = await MergeIntoAsync(retrieval.BestMatch, memory);
            return new MemorySaveResult(merged, SaveAction.Updated, retrieval.BestMatch, retrieval.NearMatches, retrieval.Reason, retrieval.MatchScore);
        }

        await CreateAsync(memory);
        return new MemorySaveResult(memory, SaveAction.Created, null, retrieval.NearMatches, retrieval.Reason, retrieval.MatchScore);
    }

    private async Task<MemorySaveResult> SaveJudgedAsync(Memory memory, Retrieval retrieval)
    {
        // Judge candidates come from the same recall results memory_recall uses: an FTS
        // porter+trigram scope of the incoming content. This replaces the older heuristic
        // near-match band so the judge weighs memories that actually live in the store,
        // ranked by relevance, instead of memories that happened to cross the 0.35
        // vocabulary threshold.
        var candidates = await RecallJudgeCandidatesAsync(memory);
        if (candidates.Count == 0)
        {
            // Nothing recall surfaced, so there is nothing to judge and no call to spend.
            await CreateAsync(memory);
            return new MemorySaveResult(memory, SaveAction.Created, null, retrieval.NearMatches, "new-memory: judge enabled, no candidate to judge", null);
        }

        SemanticJudgement verdict;
        try
        {
            using var timeout = new CancellationTokenSource(_smartSave.JudgeTimeout);
            verdict = await _judge!.JudgeAsync(memory, candidates, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout. Fall back to the heuristic so deduplication keeps working, and say
            // so in Reason. Creating here would be non-destructive, but it would also mean
            // a slow provider silently disables dedup for every save, not just the
            // ambiguous ones -- that is a regression, not a safe default.
            return await FallBackToHeuristicAsync(memory, retrieval, $"timed out after {_smartSave.JudgeTimeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            // A judge is an untrusted external boundary: transport, parsing and provider
            // faults all land here. Same reasoning as the timeout: degrade to today's
            // behaviour rather than to no dedup at all, and record the failure so a
            // judge that never works is diagnosable instead of silent.
            return await FallBackToHeuristicAsync(memory, retrieval, $"{ex.GetType().Name}: {Sanitize(ex.Message)}");
        }

        if (!verdict.HasTarget)
        {
            await CreateAsync(memory);
            return new MemorySaveResult(memory, SaveAction.Created, null, retrieval.NearMatches, Describe(verdict, null), null);
        }

        var target = await _storage.GetByIdAsync(verdict.TargetId!.Value);
        if (target is null)
        {
            // The judge named a memory that is no longer there. Create rather than
            // silently merging into a different candidate the heuristic liked.
            await CreateAsync(memory);
            return new MemorySaveResult(memory, SaveAction.Created, null, retrieval.NearMatches, $"judge: target '{verdict.TargetId}' vanished before merge; created", null);
        }

        var mergedResult = await MergeIntoAsync(target, memory);
        var score = retrieval.BestMatch is not null && retrieval.BestMatch.Id == target.Id
            ? retrieval.MatchScore
            : null;
        return new MemorySaveResult(mergedResult, SaveAction.Updated, target, retrieval.NearMatches, Describe(verdict, target), score);
    }

    /// <summary>
    /// Applies the pre-judge heuristic decision after the judge could not be used, tagging
    /// the reason so "the judge never answers" is visible in the save response instead of
    /// looking like a clean create.
    /// </summary>
    private async Task<MemorySaveResult> FallBackToHeuristicAsync(
        Memory memory,
        Retrieval retrieval,
        string kind)
    {
        var note = $"judge-failed: {kind}; fell back to the heuristic decision";

        // Information, not Debug: "the judge is not working" is exactly the thing an
        // operator needs to see while tailing, because it is invisible in every save
        // response once someone stops reading them.
        _logger?.LogInformation(
            "Judge unavailable ({Kind}); saving {IncomingId} using the heuristic, which chose {Outcome} (best score {BestScore}, {CandidateCount} candidate(s))",
            kind,
            memory.Id,
            retrieval.BestMatch is null ? "create" : "merge into " + retrieval.BestMatch.Id,
            retrieval.MatchScore,
            retrieval.JudgeCandidates.Count);

        if (retrieval.BestMatch is not null)
        {
            var merged = await MergeIntoAsync(retrieval.BestMatch, memory);
            return new MemorySaveResult(merged, SaveAction.Updated, retrieval.BestMatch, retrieval.NearMatches, note, retrieval.MatchScore);
        }

        await CreateAsync(memory);
        return new MemorySaveResult(memory, SaveAction.Created, null, retrieval.NearMatches, note, null);
    }

    /// <summary>
    /// Builds the judge's candidate set from the same recall results memory_recall uses:
    /// an FTS porter+trigram scope of the incoming content. This replaces the older
    /// heuristic near-match band so the judge weighs memories that actually live in the
    /// store, ranked by relevance, instead of memories that happened to cross the 0.35
    /// vocabulary threshold.
    /// </summary>
    private async Task<IReadOnlyCollection<Memory>> RecallJudgeCandidatesAsync(Memory incoming)
    {
        IReadOnlyCollection<MemorySearchResult> hits;
        try
        {
            hits = await _index.SearchAsync(incoming.Content);
        }
        catch
        {
            // A failed search must not break saving: treat it as "no recall candidates",
            // which keeps the save on the historical no-judge create path.
            return Array.Empty<Memory>();
        }

        var candidates = new List<Memory>(hits.Count);
        foreach (var hit in hits)
        {
            var memory = await _storage.GetByIdAsync(hit.Id);
            if (memory is null)
            {
                continue;
            }

            if (memory.Status != MemoryStatus.Active)
            {
                continue;
            }

            if (memory.Type != incoming.Type)
            {
                continue;
            }

            candidates.Add(memory);
            if (candidates.Count >= 5)
            {
                break;
            }
        }

        return candidates;
    }

    private static string Sanitize(string message)
    {
        // Judge errors can carry transport details. Strip anything that looks like a
        // header value or API key before it ends up in a Reason the user may share.
        if (string.IsNullOrEmpty(message))
        {
            return "no details";
        }

        var sanitized = message
            .Replace("apiKey", "<redacted>", StringComparison.OrdinalIgnoreCase)
            .Replace("api_key", "<redacted>", StringComparison.OrdinalIgnoreCase)
            .Replace("authorization", "<redacted>", StringComparison.OrdinalIgnoreCase);

        return sanitized.Length > 220
            ? string.Concat(sanitized.AsSpan(0, 220), "...")
            : sanitized;
    }

    private static string Describe(SemanticJudgement verdict, Memory? target)
    {
        var relation = verdict.Relation.ToString().ToLowerInvariant();
        var into = target is null ? string.Empty : $" into '{target.Id}'";
        return $"judge: {relation}{into} (confidence {verdict.Confidence:F2}) - {verdict.Reason}";
    }

    private async Task CreateAsync(Memory memory)
    {
        await _storage.SaveAsync(memory);
        await _index.IndexAsync(memory);
    }

    public async Task<Memory?> FindActiveSimilarAsync(Memory memory, double? threshold = null)
    {
        var retrieval = await RetrieveAsync(memory, threshold);
        return retrieval.BestMatch;
    }

    private static string NormalizeContent(string content) => content.Trim();

    private static MemoryNearMatch ToNearMatch(Memory memory, double score)
    {
        var preview = memory.Content.Length > 120
            ? string.Concat(memory.Content.AsSpan(0, 120), "...")
            : memory.Content;

        return new MemoryNearMatch(
            memory.Id,
            preview,
            Math.Round(score, 4),
            memory.Type,
            memory.Tags);
    }

    /// <summary>
    /// What the heuristic surfaced for one incoming memory. <paramref name="NearMatches"/>
    /// keeps the historical response shape (best match excluded, because it was already
    /// reported as the match); <paramref name="JudgeCandidates"/> is the set a judge must
    /// rule on and deliberately INCLUDES the best match, since the confident pick is
    /// exactly the one worth checking.
    /// </summary>
    private readonly record struct Retrieval(
        Memory? BestMatch,
        bool IsExactMatch,
        IReadOnlyCollection<MemoryNearMatch> NearMatches,
        IReadOnlyCollection<MemoryNearMatch> JudgeCandidates,
        string Reason,
        double? MatchScore);

    private async Task<Retrieval> RetrieveAsync(Memory incoming, double? duplicateThreshold = null)
    {
        var activeThreshold = duplicateThreshold ?? _smartSave.DuplicateThreshold;
        var normalized = NormalizeContent(incoming.Content);
        var all = await _storage.ListAllAsync();
        Memory? bestMatch = null;
        double bestScore = 0.0;
        bool isExactMatch = false;
        var candidatesWithScores = new List<(Memory Memory, double Score)>();

        foreach (var candidate in all)
        {
            if (candidate.Status != MemoryStatus.Active)
                continue;
            if (candidate.Type != incoming.Type)
                continue;

            if (string.Equals(NormalizeContent(candidate.Content), normalized, StringComparison.OrdinalIgnoreCase))
            {
                bestMatch = candidate;
                bestScore = 1.0;
                isExactMatch = true;
                continue;
            }

            if (!_smartSave.EnableFuzzyMatch)
                continue;

            var score = MemorySimilarity.CalculateSimilarity(candidate.Content, incoming.Content);
            if (score >= activeThreshold && score > bestScore)
            {
                bestMatch = candidate;
                bestScore = score;
            }

            if (score >= _smartSave.NearMatchThreshold)
            {
                candidatesWithScores.Add((candidate, score));
            }
        }

        var nearMatches = candidatesWithScores
            .Where(c => bestMatch == null || c.Memory.Id != bestMatch.Id)
            .OrderByDescending(c => c.Score)
            .Take(5)
            .Select(c => ToNearMatch(c.Memory, c.Score))
            .ToList()
            .AsReadOnly();

        // The exact match short-circuits the retrieval loop, so it never reached
        // candidatesWithScores and is put back for the judge. A byte-identical memory
        // is merged without a call, but a confident fuzzy pick is the one most worth a
        // second opinion.
        var judgeList = candidatesWithScores
            .Where(c => !isExactMatch || bestMatch == null || c.Memory.Id != bestMatch.Id)
            .OrderByDescending(c => c.Score)
            .Take(4)
            .Select(c => ToNearMatch(c.Memory, c.Score))
            .ToList();

        if (isExactMatch && bestMatch is not null)
        {
            judgeList.Insert(0, ToNearMatch(bestMatch, 1.0));
        }

        var judgeCandidates = judgeList.AsReadOnly();

        string reason;
        double? matchScore = null;

        if (bestMatch is not null)
        {
            if (isExactMatch)
            {
                reason = $"exact-match: content is identical to active memory '{bestMatch.Id}'";
                matchScore = 1.0;
            }
            else
            {
                var roundedScore = Math.Round(bestScore, 4);
                reason = $"fuzzy-match: jaccard similarity {roundedScore} >= threshold {activeThreshold} with active memory '{bestMatch.Id}'";
                matchScore = roundedScore;
            }
        }
        else if (nearMatches.Count > 0)
        {
            var top = nearMatches[0];
            reason = $"new-memory: closest match score {top.Score} < threshold {activeThreshold} with memory '{top.Id}'";
            matchScore = top.Score;
        }
        else
        {
            reason = "new-memory: no similar active memory found in scope";
        }

        return new Retrieval(bestMatch, isExactMatch, nearMatches, judgeCandidates, reason, matchScore);
    }

    private async Task<Memory> MergeIntoAsync(Memory existing, Memory incoming)
    {
        var mergedTags = Memory.NormalizeTags(
            existing.Tags.Concat(incoming.Tags));

        var merged = new Memory(
            existing.Type,
            incoming.Content,
            mergedTags,
            incoming.Source ?? existing.Source,
            existing.Status,
            existing.Id,
            existing.CreatedAt,
            DateTimeOffset.UtcNow);

        await _storage.SaveAsync(merged);
        await _index.IndexAsync(merged);
        return merged;
    }

    public Task<Memory?> GetByIdAsync(MemoryId id) => _storage.GetByIdAsync(id);

    public async Task<Memory?> UpdateAsync(MemoryId id, string? content = null, MemoryType? type = null, string[]? tags = null, string? source = null, MemoryStatus? status = null)
    {
        var existing = await _storage.GetByIdAsync(id);
        if (existing is null)
        {
            return null;
        }

        // Type is immutable, so we need to create a new Memory object
        var updatedType = type ?? existing.Type;
        var updatedContent = content ?? existing.Content;
        var updatedTags = tags ?? existing.Tags.ToArray();
        var updatedSource = source ?? existing.Source;
        var updatedStatus = status ?? existing.Status;

        var updated = new Memory(
            updatedType,
            updatedContent,
            updatedTags,
            updatedSource,
            updatedStatus,
            existing.Id,
            existing.CreatedAt,
            DateTimeOffset.UtcNow);

        await _storage.SaveAsync(updated);
        await _index.IndexAsync(updated);
        return updated;
    }

    public async Task<bool> DeleteAsync(MemoryId id)
    {
        if (!await _storage.DeleteAsync(id))
        {
            return false;
        }
        await _index.RemoveAsync(id);
        return true;
    }

    public Task<IReadOnlyCollection<Memory>> ListAllAsync() => _storage.ListAllAsync();

    public Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query) => _index.SearchAsync(query);

    public async Task RebuildIndexAsync()
    {
        var memories = await _storage.ListAllAsync();
        await _index.RebuildAsync(memories);
    }
}

