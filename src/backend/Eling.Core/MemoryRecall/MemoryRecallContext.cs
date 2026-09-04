namespace Eling.Core;

/// <summary>
/// Input context for <see cref="IMemoryRecallService.RecallAsync"/>. Mirrors
/// the MCP DTO <c>MemoryRecallContextInput</c> but is decoupled so the
/// application layer can evolve independently.
/// </summary>
public sealed record MemoryRecallContext(
    IReadOnlyCollection<string> Topics,
    string? FilePath,
    string? ProjectRoot);
