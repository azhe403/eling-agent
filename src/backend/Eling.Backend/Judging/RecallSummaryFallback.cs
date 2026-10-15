using Eling.Core.Memory;

namespace Eling.Backend.Judging;

/// <summary>
/// Deterministic digest for recall overflow, used when the semantic
/// summarizer is not configured, times out, or returns nothing usable.
/// Mirrors the save pipeline's "judge-failed; fell back to the heuristic"
/// posture: the feature keeps working, and the degraded source is visible.
/// </summary>
/// <remarks>
/// Each item becomes one compact <c>[Type] content</c> fragment clipped at
/// <see cref="ItemClipCharacters"/>, and fragments are grouped into short
/// paragraphs rather than crammed into a single block — a long record
/// simply consumes more of its paragraph. No ids, no invented prose.
/// </remarks>
public static class RecallSummaryFallback
{
    /// <summary>Maximum items represented in one digest.</summary>
    public const int MaxItems = 10;

    /// <summary>Maximum paragraphs in one digest.</summary>
    public const int MaxParagraphs = 5;

    private const int ItemClipCharacters = 180;
    private const int ItemsPerParagraph = 3;

    /// <summary>
    /// Builds digest paragraphs from <paramref name="items"/>, best-relevant
    /// first. Always returns at least one paragraph for a non-empty input.
    /// </summary>
    public static IReadOnlyList<string> Build(IReadOnlyCollection<Memory> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return [];
        }

        var fragments = items
            .Take(MaxItems)
            .Select(m => $"[{m.Type}] {Clip(m.Content)}")
            .ToList();

        var paragraphs = new List<string>();
        for (var i = 0; i < fragments.Count; i += ItemsPerParagraph)
        {
            paragraphs.Add(string.Join(" ", fragments.Skip(i).Take(ItemsPerParagraph)));
        }

        return paragraphs.Take(MaxParagraphs).ToList();
    }

    private static string Clip(string content)
    {
        var text = content.Trim();
        if (text.Length <= ItemClipCharacters)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', ItemClipCharacters);
        if (cut < ItemClipCharacters / 2)
        {
            cut = ItemClipCharacters;
        }

        return string.Concat(text.AsSpan(0, cut), "...");
    }
}
