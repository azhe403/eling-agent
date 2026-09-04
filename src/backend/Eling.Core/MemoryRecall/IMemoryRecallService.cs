namespace Eling.Core;

/// <summary>
/// Builds a recall payload that bundles topic-based recall, recently updated
/// active memories, and outstanding intentions so MCP clients can hydrate
/// context on demand at any point in a conversation.
/// </summary>
public interface IMemoryRecallService
{
    Task<MemoryRecallResult> RecallAsync(
        MemoryRecallContext? context,
        int recallLimit = 10,
        int recentLimit = 10,
        string? scope = null,
        CancellationToken cancellationToken = default);
}
