namespace Eling.Core.Memory;

/// <summary>
/// Decides whether an incoming memory revises, contradicts, or is unrelated to the
/// active memories it resembles.
/// </summary>
/// <remarks>
/// The heuristic scorer (<see cref="MemorySimilarity"/>) is deliberately demoted to a
/// retriever: it can only count shared tokens, so it is confidently wrong on memories
/// that share a shape but differ in meaning — measured at 0.75 for "User prefers dark
/// mode" against "User prefers light mode", which would silently overwrite one
/// preference with its opposite. Retrieval stays cheap and exhaustive; this interface
/// supplies the judgement the heuristic cannot. Implementations are an external
/// boundary and may fail, so callers must define their own safe fallback.
/// </remarks>
public interface ISemanticJudge
{
    /// <summary>
    /// Judges <paramref name="incoming"/> against every candidate the retriever surfaced.
    /// </summary>
    /// <param name="incoming">The memory being saved.</param>
    /// <param name="candidates">Active memories worth judging, best-scoring first.</param>
    /// <param name="cancellationToken">Cancels the call; implementations should also apply their own timeout.</param>
    /// <returns>
    /// A verdict naming at most one <see cref="SemanticJudgement.TargetId"/>, or
    /// <see cref="SemanticRelation.Unrelated"/> when nothing applies.
    /// </returns>
    Task<SemanticJudgement> JudgeAsync(
        Memory incoming,
        IReadOnlyCollection<Memory> candidates,
        CancellationToken cancellationToken);
}
