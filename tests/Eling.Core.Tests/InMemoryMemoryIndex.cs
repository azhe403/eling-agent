using Eling.Core;

namespace Eling.Core.Tests;

public sealed class InMemoryMemoryIndex : IMemoryIndex
{
    private readonly Dictionary<MemoryId, Memory> _items = new();
    private readonly object _lock = new();

    public Task IndexAsync(Memory memory)
    {
        lock (_lock)
        {
            _items[memory.Id] = memory;
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(MemoryId id)
    {
        lock (_lock)
        {
            _items.Remove(id);
        }
        return Task.CompletedTask;
    }

    public Task RebuildAsync(IEnumerable<Memory> memories)
    {
        lock (_lock)
        {
            _items.Clear();
            foreach (var memory in memories)
            {
                _items[memory.Id] = memory;
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query)
    {
        lock (_lock)
        {
            IReadOnlyCollection<MemorySearchResult> results = _items.Values
                .Where(m => m.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(m => new MemorySearchResult(m.Id, 1.0))
                .ToList();
            return Task.FromResult(results);
        }
    }
}
