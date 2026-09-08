using Eling.Core;

namespace Eling.Core.Tests;

/// <summary>
/// In-memory implementation of <see cref="IMemoryStorage"/> used by unit tests
/// so the Smart Save / maintenance flow can be exercised without touching the
/// filesystem.
/// </summary>
public sealed class InMemoryMemoryStorage : IMemoryStorage
{
    private readonly Dictionary<MemoryId, Memory> _items = new();
    private readonly object _lock = new();

    public Task SaveAsync(Memory memory)
    {
        lock (_lock)
        {
            _items[memory.Id] = memory;
        }
        return Task.CompletedTask;
    }

    public Task<Memory?> GetByIdAsync(MemoryId id)
    {
        lock (_lock)
        {
            _items.TryGetValue(id, out var memory);
            return Task.FromResult<Memory?>(memory);
        }
    }

    public Task<IReadOnlyCollection<Memory>> ListAllAsync()
    {
        lock (_lock)
        {
            IReadOnlyCollection<Memory> snapshot = _items.Values.ToList();
            return Task.FromResult(snapshot);
        }
    }

    public Task<bool> DeleteAsync(MemoryId id)
    {
        lock (_lock)
        {
            return Task.FromResult(_items.Remove(id));
        }
    }
}
