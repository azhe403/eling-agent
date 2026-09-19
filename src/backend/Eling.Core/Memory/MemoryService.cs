using Eling.Core.Memory.Storage;

namespace Eling.Core.Memory;

public class MemoryService : IMemoryService
{
    private readonly IMemoryStorage _storage;
    private readonly IMemoryIndex _index;
    private readonly SmartSaveOptions _smartSave;

    public MemoryService(IMemoryStorage storage, IMemoryIndex index)
        : this(storage, index, new SmartSaveOptions())
    {
    }

    public MemoryService(IMemoryStorage storage, IMemoryIndex index, SmartSaveOptions smartSave)
    {
        _storage = storage;
        _index = index;
        _smartSave = smartSave;
    }

    public async Task<SaveResult> SaveAsync(Memory memory)
    {
        var (existing, nearMatches, reason, matchScore) = await FindActiveSimilarAndNearMatchesAsync(memory);
        if (existing is not null)
        {
            var merged = await MergeIntoAsync(existing, memory);
            return new SaveResult(merged, SaveAction.Updated, existing, nearMatches, reason, matchScore);
        }

        await _storage.SaveAsync(memory);
        await _index.IndexAsync(memory);
        return new SaveResult(memory, SaveAction.Created, null, nearMatches, reason, matchScore);
    }

    public async Task<Memory?> FindActiveSimilarAsync(Memory memory, double? threshold = null)
    {
        var (bestMatch, _, _, _) = await FindActiveSimilarAndNearMatchesAsync(memory, threshold);
        return bestMatch;
    }

    private static string NormalizeContent(string content) => content.Trim();

    private async Task<(Memory? BestMatch, IReadOnlyCollection<NearMatch> NearMatches, string Reason, double? MatchScore)> FindActiveSimilarAndNearMatchesAsync(Memory incoming, double? duplicateThreshold = null)
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
            .Select(c => new NearMatch(
                c.Memory.Id,
                c.Memory.Content.Length > 120 ? string.Concat(c.Memory.Content.AsSpan(0, 120), "...") : c.Memory.Content,
                Math.Round(c.Score, 4),
                c.Memory.Type,
                c.Memory.Tags))
            .ToList()
            .AsReadOnly();

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

        return (bestMatch, nearMatches, reason, matchScore);
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

