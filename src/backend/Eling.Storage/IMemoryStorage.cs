using Eling.Core;

namespace Eling.Storage;

public interface IMemoryStorage
{
    Task SaveAsync(Memory memory);
    Task<Memory?> GetByIdAsync(MemoryId id);
    Task<bool> DeleteAsync(MemoryId id);
    Task<IReadOnlyCollection<Memory>> ListAllAsync();
}
