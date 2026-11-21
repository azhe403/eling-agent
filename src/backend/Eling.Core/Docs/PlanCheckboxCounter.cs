namespace Eling.Core.Docs;

using System.Text.RegularExpressions;

/// <summary>
/// Counts checkboxes in plan documents. Fenced code blocks are excluded.
/// </summary>
public static class PlanCheckboxCounter
{
    private static readonly Regex CheckboxRegex = new(
        @"^\s*-\s*\[([ xX])\]\s",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex FenceRegex = new(
        @"^```.*?^```",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Counts total and done checkboxes in the content.
    /// Returns (total, done).
    /// </summary>
    public static (int Total, int Done) Count(string content)
    {
        // Remove fenced code blocks
        var withoutFences = FenceRegex.Replace(content, "");

        var matches = CheckboxRegex.Matches(withoutFences);
        int total = 0, done = 0;
        foreach (Match m in matches)
        {
            total++;
            if (m.Groups[1].Value.Equals("x", StringComparison.OrdinalIgnoreCase))
            {
                done++;
            }
        }
        return (total, done);
    }

    /// <summary>
    /// Counts checkboxes per section.
    /// </summary>
    public static IReadOnlyDictionary<string, (int Total, int Done)> CountPerSection(
        string content,
        IReadOnlyList<SectionChunk> sections)
    {
        var lines = content.Split('\n');
        var result = new Dictionary<string, (int Total, int Done)>();

        foreach (var section in sections)
        {
            var sectionLines = lines
                .Skip(section.LineStart - 1)
                .Take(section.LineEnd - section.LineStart + 1)
                .ToArray();
            var sectionContent = string.Join('\n', sectionLines);
            
            // Remove fenced code
            var withoutFences = FenceRegex.Replace(sectionContent, "");
            
            var matches = CheckboxRegex.Matches(withoutFences);
            int total = 0, done = 0;
            foreach (Match m in matches)
            {
                total++;
                if (m.Groups[1].Value.Equals("x", StringComparison.OrdinalIgnoreCase))
                    done++;
            }
            result[section.Anchor] = (total, done);
        }

        return result;
    }
}