namespace Eling.Core;

public class MemoryService : IMemoryService
{
    private readonly IMemoryStorage _storage;
    private readonly IMemoryIndex _index;

    public MemoryService(IMemoryStorage storage, IMemoryIndex index)
    {
        _storage = storage;
        _index = index;
    }

    public async Task<SaveResult> SaveAsync(Memory memory)
    {
        // Dedup: if an active memory with identical content (ignoring case and
        // leading/trailing whitespace) already exists, update it in place instead
        // of inserting a duplicate. Only active memories are deduplicated so that
        // archived/superseded entries never swallow a new save.
        var normalized = NormalizeContent(memory.Content);
        var existing = await FindActiveByContentAsync(normalized);
        if (existing is not null)
        {
            var merged = await MergeIntoAsync(existing, memory);
            return new SaveResult(merged, SaveAction.Updated);
        }

        await _storage.SaveAsync(memory);
        await _index.IndexAsync(memory);
        return new SaveResult(memory, SaveAction.Created);
    }

    private static string NormalizeContent(string content) => content.Trim();

    private async Task<Memory?> FindActiveByContentAsync(string normalizedContent)
    {
        var all = await _storage.ListAllAsync();
        foreach (var candidate in all)
        {
            if (candidate.Status == MemoryStatus.Active &&
                string.Equals(NormalizeContent(candidate.Content), normalizedContent, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
        return null;
    }

    private async Task<Memory> MergeIntoAsync(Memory existing, Memory incoming)
    {
        var mergedTags = existing.Tags
            .Concat(incoming.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

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

