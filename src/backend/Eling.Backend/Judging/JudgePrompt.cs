using System.Globalization;
using System.Text;
using Eling.Core.Memory;

namespace Eling.Backend.Judging;

/// <summary>
/// Builds the judge's prompt and turns its answer back into a
/// <see cref="SemanticJudgement"/>. Pure functions, so the wording is unit-testable
/// without a provider.
/// </summary>
/// <remarks>
/// The wording is the one validated against a real model: it states which record is
/// older, offers the six relations, and explicitly warns against being fooled by
/// surface wording similarity. It deliberately does NOT suggest that the records are
/// related, because that would only measure confirmation bias. Candidates are labelled
/// C1..Cn and echoed back as a label, never as a ULID.
/// </remarks>
public static class JudgePrompt
{
    private const int MaxContentCharacters = 4000;

    /// <summary>System prompt carrying the relation definitions and the answer format.</summary>
    public const string SystemInstructions = """
        You are classifying the relationship between a record being saved and existing records in a
        knowledge store. Read each pair carefully. Do not assume they are related, and do not assume
        they are unrelated.

        Classify the relationship. Choose exactly one:
        - identical: same meaning and same guidance
        - revision: same topic and intent, with the incoming record refining or correcting the existing one
        - contradiction: they give conflicting guidance on the same topic, so one supersedes the other
        - superset: the incoming record contains everything an existing one says, plus more
        - subset: the incoming record is a narrower version of an existing one
        - unrelated: different topics, no meaningful relationship

        Two records may share a sentence shape, framing words, or a common date while being completely
        unrelated. Never merge on surface similarity alone. Equally, do not call records unrelated
        just because they word the same idea differently.

        If the incoming record corrects, narrows, or reverses existing guidance, say so plainly and name
        the existing record it supersedes.

        Respond with JSON only, no prose and no code fence, using exactly these keys:
        {"relation":"<one option>","confidence":<number between 0 and 1>,"reason":"<one sentence>","target":"<label>"}

        Set "target" to the label of the single most relevant existing record, or "none" when the
        relation is unrelated.
        """;

    /// <summary>
    /// Renders the user message describing the incoming record and the labelled candidates.
    /// The incoming record is always labelled C0 so "which one does this supersede" is unambiguous.
    /// </summary>
    public static string Build(Memory incoming, IReadOnlyCollection<Memory> candidates)
    {
        var builder = new StringBuilder();
        var list = AsList(candidates);

        builder.AppendLine("INCOMING RECORD (label C0):");
        builder.AppendLine(Truncate(incoming.Content));
        builder.AppendLine();
        builder.AppendLine($"Type: {incoming.Type}");
        builder.AppendLine();
        builder.AppendLine($"EXISTING RECORD CANDIDATES ({list.Count}):");

        for (var i = 0; i < list.Count; i++)
        {
            var candidate = list[i];
            builder.AppendLine();
            builder.AppendLine($"C{i + 1}:");
            builder.AppendLine(Truncate(candidate.Content));
            builder.AppendLine($"Type: {candidate.Type}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Converts a provider answer into a verdict, or <c>null</c> when the answer cannot be
    /// trusted. A null here is not an error: the caller creates the memory instead, which is
    /// the non-destructive direction.
    /// </summary>
    /// <param name="raw">The text the model returned.</param>
    /// <param name="candidates">Candidates in the same order the prompt listed them.</param>
    public static SemanticJudgement? Parse(string raw, IReadOnlyCollection<Memory> candidates)
    {
        if (string.IsNullOrWhiteSpace(raw) || candidates.Count == 0)
        {
            return null;
        }

        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            return null;
        }

        JudgeVerdict? verdict;
        try
        {
            verdict = System.Text.Json.JsonSerializer.Deserialize(
                json,
                SemanticJudgeJsonContext.Default.JudgeVerdict);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (verdict is null || !Enum.TryParse<SemanticRelation>(verdict.Relation, ignoreCase: true, out var relation))
        {
            return null;
        }

        var confidence = Math.Clamp(verdict.Confidence ?? 0.0, 0.0, 1.0);
        var reason = string.IsNullOrWhiteSpace(verdict.Reason) ? "no reason given" : verdict.Reason.Trim();
        var target = ResolveTarget(verdict.Target, candidates);

        // A relation that claims a target the model did not name is not actionable.
        if (relation != SemanticRelation.Unrelated && target is null)
        {
            relation = SemanticRelation.Unrelated;
            reason = $"{reason} (no usable target label, treated as unrelated)";
        }

        // Keep the invariant in the type: an Unrelated verdict never carries a target,
        // so a model that names one anyway cannot leave a misleading value behind.
        if (relation == SemanticRelation.Unrelated)
        {
            target = null;
        }

        // The verdict travels to the save response, so it is observable without reading
        // logs. The model-authored reason can echo memory text, so it stays truncated.
        return new SemanticJudgement(relation, confidence, Truncate(reason, 400), target);
    }

    private static MemoryId? ResolveTarget(string? label, IReadOnlyCollection<Memory> candidates)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var trimmed = label.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!trimmed.StartsWith('C') || !int.TryParse(trimmed[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return null;
        }

        var list = AsList(candidates);

        // C0 is the incoming record itself, never a merge target.
        if (index < 1 || index > list.Count)
        {
            return null;
        }

        return list[index - 1].Id;
    }

    private static IReadOnlyList<Memory> AsList(IReadOnlyCollection<Memory> candidates)
        => candidates as IReadOnlyList<Memory> ?? [.. candidates];

    /// <summary>
    /// Pulls the outermost JSON object out of a response that may be wrapped in prose or a
    /// code fence, which models do despite being asked not to.
    /// </summary>
    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return raw[start..(end + 1)];
    }

    private static string Truncate(string content)
        => content.Length > MaxContentCharacters
            ? string.Concat(content.AsSpan(0, MaxContentCharacters), "...")
            : content;

    private static string Truncate(string text, int maxLength)
        => text.Length > maxLength
            ? string.Concat(text.AsSpan(0, maxLength), "...")
            : text;
}
