using Eling.Core;

namespace Eling.Core;

public interface IIntentionStorage
{
    Task SaveAsync(Intention intention);
    Task<Intention?> GetByIdAsync(MemoryId id);
    Task<bool> DeleteAsync(MemoryId id);
    Task<IReadOnlyCollection<Intention>> ListAllAsync();
}
