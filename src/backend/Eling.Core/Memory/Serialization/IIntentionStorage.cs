namespace Eling.Core.Memory.Serialization;

public interface IIntentionStorage
{
    Task SaveAsync(Intention.Intention intention);
    Task<Intention.Intention?> GetByIdAsync(MemoryId id);
    Task<bool> DeleteAsync(MemoryId id);
    Task<IReadOnlyCollection<Intention.Intention>> ListAllAsync();
}
