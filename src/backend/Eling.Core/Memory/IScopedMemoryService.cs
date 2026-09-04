using Eling.Core;

namespace Eling.Core;

public interface IScopedMemoryService
{
    Task<ScopedSaveResult> SaveAsync(Memory memory, string? scope = null);
    Task<ScopedMemory?> GetByIdAsync(MemoryReference reference);
    Task<ScopedMemory?> GetByIdAsync(MemoryId id, string? scope);
    Task<bool> DeleteAsync(MemoryReference reference);
    Task<IReadOnlyCollection<ScopedMemory>> ListAsync(string? scope = null, MemoryStatus? status = null);
    Task<IReadOnlyCollection<ScopedSearchResult>> SearchAsync(string query, string? scope = null, int? limit = null);
    Task<ScopedMemory?> UpdateAsync(MemoryReference reference, string? content = null, MemoryType? type = null, string[]? tags = null, string? source = null, MemoryStatus? status = null);
    Task RebuildIndexAsync(string? scope = null);

    // Copy / Promote between scopes
    Task<ScopedMemory?> CopyToProjectAsync(MemoryReference source, string targetProjectRoot);
    Task<ScopedMemory?> PromoteToGlobalAsync(MemoryReference source);

    // Raw services for isolation checks
    IMemoryService ProjectService { get; }
    IMemoryService GlobalService { get; }
    string? ProjectRoot { get; }
}

