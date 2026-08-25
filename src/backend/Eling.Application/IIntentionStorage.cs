using Eling.Core;

namespace Eling.Application;

public interface IIntentionStorage
{
    Task SaveAsync(Intention intention);
    Task<Intention?> GetByIdAsync(MemoryId id);
    Task<bool> DeleteAsync(MemoryId id);
    Task<IReadOnlyCollection<Intention>> ListAllAsync();
}