namespace Eling.Core;

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
        var existing = await FindActiveSimilarAsync(memory);
        if (existing is not null)
        {
            var merged = await MergeIntoAsync(existing, memory);
            return new SaveResult(merged, SaveAction.Updated, existing);
        }

        await _storage.SaveAsync(memory);
        await _index.IndexAsync(memory);
        return new SaveResult(memory, SaveAction.Created);
    }

    private static string NormalizeContent(string content) => content.Trim();

    private async Task<Memory?> FindActiveSimilarAsync(Memory incoming)
    {
        var normalized = NormalizeContent(incoming.Content);
        var all = await _storage.ListAllAsync();
        Memory? bestMatch = null;
        double bestScore = 0.0;

        foreach (var candidate in all)
        {
            if (candidate.Status != MemoryStatus.Active)
                continue;
            if (candidate.Type != incoming.Type)
                continue;

            if (string.Equals(NormalizeContent(candidate.Content), normalized, StringComparison.OrdinalIgnoreCase))
                return candidate;

            if (!_smartSave.EnableFuzzyMatch)
                continue;

            var score = MemorySimilarity.CalculateSimilarity(candidate.Content, incoming.Content);
            if (score >= _smartSave.DuplicateThreshold && score > bestScore)
            {
                bestMatch = candidate;
                bestScore = score;
            }
        }

        return bestMatch;
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

