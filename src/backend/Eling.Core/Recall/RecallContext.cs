namespace Eling.Core.Recall;

/// <summary>
/// Input context for <see cref="IRecallService.RecallAsync"/>. Mirrors
/// the MCP DTO <c>RecallContextInput</c> but is decoupled so the
/// application layer can evolve independently.
/// </summary>
public sealed record RecallContext(
    IReadOnlyCollection<string> Topics,
    string? FilePath,
    string? ProjectRoot);
