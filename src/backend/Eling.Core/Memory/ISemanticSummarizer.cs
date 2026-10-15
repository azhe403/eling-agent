namespace Eling.Core.Memory;

/// <summary>
/// Summarizes a bounded set of memories into digest paragraphs. Used by the
/// recall pipeline to summarize the relevant hits that exceeded the recall
/// limit instead of dropping them.
/// </summary>
/// <remarks>
/// Implementations are an external boundary (the same provider the semantic
/// judge uses) and may fail, so callers must define their own safe fallback —
/// a deterministic digest — and record which source produced the result.
/// </remarks>
public interface ISemanticSummarizer
{
    /// <summary>
    /// Summarizes <paramref name="items"/> into a small number of paragraphs.
    /// </summary>
    /// <param name="items">Memories to summarize, most relevant first. Never empty.</param>
    /// <param name="cancellationToken">Cancels the call; implementations should also apply their own timeout.</param>
    /// <returns>
    /// A summary with its paragraphs. When the answer is unusable the
    /// implementation returns empty paragraphs rather than throwing, so the
    /// caller can fall back deterministically.
    /// </returns>
    Task<MemorySummary> SummarizeAsync(
        IReadOnlyCollection<Memory> items,
        CancellationToken cancellationToken);
}
