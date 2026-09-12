using Eling.Core;
using Eling.Core.Memory;
using Eling.Core.Memory.Storage;

namespace Eling.Core.Tests;

/// <summary>
/// In-memory implementation of <see cref="IMemoryStorage"/> used by unit tests
/// so the Smart Save / maintenance flow can be exercised without touching the
/// filesystem.
/// </summary>
public sealed class InMemoryMemoryStorage : IMemoryStorage
{
    private readonly Dictionary<MemoryId, Memory.Memory> _items = new();
    private readonly object _lock = new();

    public Task SaveAsync(Memory.Memory memory)
    {
        lock (_lock)
        {
            _items[memory.Id] = memory;
        }
        return Task.CompletedTask;
    }

    public Task<Memory.Memory?> GetByIdAsync(MemoryId id)
    {
        lock (_lock)
        {
            _items.TryGetValue(id, out var memory);
            return Task.FromResult<Memory.Memory?>(memory);
        }
    }

    public Task<IReadOnlyCollection<Memory.Memory>> ListAllAsync()
    {
        lock (_lock)
        {
            IReadOnlyCollection<Memory.Memory> snapshot = _items.Values.ToList();
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
