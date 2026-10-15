using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>
/// Digest of the relevant recall hits that exceeded the recall limit, so
/// the overflow is summarized instead of silently dropped.
/// </summary>
public sealed class RecallSummaryDto
{
    /// <summary>How many relevant hits were beyond the recall limit in total.</summary>
    [JsonPropertyName("itemCount")]
    public int ItemCount { get; set; }

    /// <summary>How many of those items this digest covers (bounded by the overflow window).</summary>
    [JsonPropertyName("summarizedCount")]
    public int SummarizedCount { get; set; }

    /// <summary>What produced the paragraphs: "judge" (semantic summarizer) or "heuristic" (deterministic digest).</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "heuristic";

    /// <summary>Digest paragraphs, grouped by theme. Multi-paragraph by design so long records are split, not crammed.</summary>
    [JsonPropertyName("paragraphs")]
    public IReadOnlyCollection<string> Paragraphs { get; set; } = [];

    /// <summary>Why the digest degraded to the heuristic source, when it did.</summary>
    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}
