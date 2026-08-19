using Eling.Core;

namespace Eling.Storage;

public interface IIntentionStorage
{
    Task SaveAsync(Intention intention);
    Task<Intention?> GetByIdAsync(MemoryId id);
    Task<bool> DeleteAsync(MemoryId id);
    Task<IReadOnlyCollection<Intention>> ListAllAsync();
}