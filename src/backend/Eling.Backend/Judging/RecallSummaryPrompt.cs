using System.Globalization;
using System.Text;
using Eling.Core.Memory;

namespace Eling.Backend.Judging;

/// <summary>
/// Builds the recall-summary prompt and turns the answer back into digest
/// paragraphs. Pure functions, so the wording is unit-testable without a
/// provider.
/// </summary>
/// <remarks>
/// The summarized set is bounded (the overflow window of a recall), and the
/// items are memory records that already exceeded the recall limit. The
/// answer is deliberately paragraphs, not JSON: it is prose an agent reads,
/// and asking for structure only invites parsing failures. Long records are
/// split across paragraphs by theme instead of being crammed into one.
/// </remarks>
public static class RecallSummaryPrompt
{
    private const int MaxContentCharacters = 4000;
    private const int MaxParagraphs = 5;
    private const int MaxParagraphCharacters = 600;

    /// <summary>System prompt describing the summary contract and answer format.</summary>
    public const string SystemInstructions = """
        You are summarizing records from a knowledge store for an agent that could not read them
        all: these records exceeded the agent's recall limit. Read every record carefully.

        Write 2 to 5 short paragraphs in plain language. Group records that share a theme into the
        same paragraph. State rules, decisions, and lessons directly as guidance the agent must
        follow; state facts as facts. Never invent content that is not in the records. Do not
        repeat the same record twice, and do not quote record labels or ids.

        Respond with paragraphs only, separated by blank lines. No preamble, no title, no
        numbering, no code fence, no JSON.
        """;

    /// <summary>
    /// Renders the user message listing the labelled records M1..Mn.
    /// </summary>
    public static string Build(IReadOnlyCollection<Memory> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var list = items as IReadOnlyList<Memory> ?? [.. items];
        var builder = new StringBuilder();

        builder.AppendLine("RECORDS THAT EXCEEDED THE RECALL LIMIT:");
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            builder.AppendLine();
            builder.AppendLine($"M{i + 1}:");
            builder.AppendLine(Truncate(item.Content));
            builder.AppendLine($"Type: {item.Type}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Converts a provider answer into digest paragraphs, or an empty list
    /// when the answer cannot be used. An empty list is not an error: the
    /// caller falls back to a deterministic digest.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var paragraphs = raw
            .Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(TruncateParagraph)
            .Take(MaxParagraphs)
            .ToList();

        return paragraphs;
    }

    private static string TruncateParagraph(string paragraph)
        => paragraph.Length > MaxParagraphCharacters
            ? string.Concat(paragraph.AsSpan(0, MaxParagraphCharacters), "...")
            : paragraph;

    private static string Truncate(string content)
        => content.Length > MaxContentCharacters
            ? string.Concat(content.AsSpan(0, MaxContentCharacters), "...")
            : content;
}
