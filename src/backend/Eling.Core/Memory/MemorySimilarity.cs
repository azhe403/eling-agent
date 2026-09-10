using System.Text.RegularExpressions;

namespace Eling.Core;

public static class MemorySimilarity
{
    private static readonly Regex TokenSplitRegex = new(@"[\s\p{P}]+", RegexOptions.Compiled);

    /// <summary>
    /// Tokenizes a string into a set of lowercase tokens (length > 1), split on whitespace and punctuation.
    /// </summary>
    /// <param name="content">The input string to tokenize.</param>
    /// <returns>A HashSet of tokens, or an empty set if the input is null/whitespace.</returns>
    public static HashSet<string> Tokenize(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var tokens = TokenSplitRegex.Split(content.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return tokens;
    }

    /// <summary>
    /// Calculates the Jaccard similarity coefficient between two strings.
    /// Returns a value between 0.0 and 1.0, where 1.0 means identical content.
    /// </summary>
    /// <param name="a">The first string to compare.</param>
    /// <param name="b">The second string to compare.</param>
    /// <returns>The Jaccard similarity score in the range [0.0, 1.0].</public>
    public static double CalculateJaccard(string a, string b)
    {
        var setA = Tokenize(a);
        var setB = Tokenize(b);

        if (setA.Count == 0 && setB.Count == 0)
            return 1.0;
        if (setA.Count == 0 || setB.Count == 0)
            return 0.0;

        var intersectionCount = setA.Intersect(setB).Count();
        var unionCount = setA.Union(setB).Count();

        return (double)intersectionCount / unionCount;
    }

    /// <summary>
    /// Calculates the Dice coefficient (Sørensen–Dice) between two strings.
    /// Rewards overlap relative to average set size: 2|A ∩ B| / (|A| + |B|).
    /// </summary>
    public static double CalculateDice(string a, string b)
    {
        var setA = Tokenize(a);
        var setB = Tokenize(b);

        if (setA.Count == 0 && setB.Count == 0)
            return 1.0;
        if (setA.Count == 0 || setB.Count == 0)
            return 0.0;

        var intersectionCount = setA.Intersect(setB).Count();
        return (2.0 * intersectionCount) / (setA.Count + setB.Count);
    }

    /// <summary>
    /// Calculates the maximum containment of one token set within the other:
    /// |A ∩ B| / min(|A|, |B|). Detects "extend" patterns where one text is a
    /// superset of the other (e.g. a memory later expanded with more detail).
    /// </summary>
    public static double CalculateMaxContainment(string a, string b)
    {
        var setA = Tokenize(a);
        var setB = Tokenize(b);

        if (setA.Count == 0 && setB.Count == 0)
            return 1.0;
        if (setA.Count == 0 || setB.Count == 0)
            return 0.0;

        var intersectionCount = setA.Intersect(setB).Count();
        return (double)intersectionCount / Math.Min(setA.Count, setB.Count);
    }

    /// <summary>
    /// Blended similarity for smart-save duplicate detection: 60% Dice
    /// (paraphrase-aware) + 40% max containment (extend-aware). Weighing
    /// containment below 1.0 prevents a short generic phrase that happens to
    /// be fully contained in a long unrelated text from scoring as a match.
    /// </summary>
    public static double CalculateSimilarity(string a, string b) =>
        (0.6 * CalculateDice(a, b)) + (0.4 * CalculateMaxContainment(a, b));
}
