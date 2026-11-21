namespace Eling.Core.Docs;

using System.Text.RegularExpressions;

/// <summary>
/// Splits a Markdown document into sections by ATX headings.
/// Fenced code blocks are never split. Anchors use GitHub-flavoured slugging
/// with `-1`/`-2` deduplication. A `_preamble` section captures content before
/// the first heading.
/// </summary>
public static class HeadingChunker
{
    private static readonly Regex HeadingRegex = new(
        @"^(#{1,6})\s+(.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex FenceRegex = new(
        @"^```.*?^```",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Chunks the document content into sections.
    /// Returns a ChunkResult containing sections and preamble content.
    /// </summary>
    public static ChunkResult Chunk(string content)
    {
        var sections = new List<SectionChunk>();
        var lines = content.Split('\n');
        var inFence = false;
        var fenceChar = '\0';

        // First pass: find all headings with their line numbers, skipping fenced code
        var headings = new List<HeadingInfo>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            
            // Track fence state
            if (line.Length >= 3 && (line.StartsWith("```") || line.StartsWith("~~~")))
            {
                var chars = line.TakeWhile(c => c == '`' || c == '~').ToArray();
                if (chars.Length >= 3)
                {
                    if (!inFence)
                    {
                        inFence = true;
                        fenceChar = chars[0];
                    }
                    else if (chars[0] == fenceChar && chars.Length >= 3)
                    {
                        inFence = false;
                        fenceChar = '\0';
                    }
                }
            }

            if (!inFence)
            {
                var match = HeadingRegex.Match(line);
                if (match.Success)
                {
                    var level = match.Groups[1].Value.Length;
                    var text = match.Groups[2].Value.Trim();
                    headings.Add(new HeadingInfo(level, text, i));
                }
            }
        }

        // Build sections
        var anchorCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lineCount = lines.Length;

        if (headings.Count == 0)
        {
            // No headings: entire document is preamble
            return new ChunkResult(sections.AsReadOnly(), content.Trim());
        }

        // Preamble: content before first heading
        var firstHeadingLine = headings[0].LineIndex;
        var preambleLines = lines.Take(firstHeadingLine).ToArray();
        var preamble = string.Join('\n', preambleLines).Trim();

        // Sections from headings
        for (int i = 0; i < headings.Count; i++)
        {
            var heading = headings[i];
            var startLine = heading.LineIndex + 1; // 1-based, content starts after heading
            var endLine = (i + 1 < headings.Count) ? headings[i + 1].LineIndex : lineCount;

            var anchor = Slugify(heading.Text, anchorCounts);
            var headingLine = $"{"#".PadRight(heading.Level, '#')} {heading.Text}";
            
            sections.Add(new SectionChunk
            {
                Anchor = anchor,
                Heading = headingLine,
                Level = heading.Level,
                LineStart = startLine,
                LineEnd = endLine,
                RawHeadingText = heading.Text,
            });
        }

        return new ChunkResult(sections.AsReadOnly(), preamble);
    }

    private static string Slugify(string text, Dictionary<string, int> counts)
    {
        // GitHub-flavoured anchor: lowercase, replace spaces/punct with -, collapse multiple
        var slug = text.ToLowerInvariant();
        slug = Regex.Replace(slug, @"[^\p{L}\p{N}\s-]", "");
        slug = Regex.Replace(slug, @"[\s-]+", "-");
        slug = slug.Trim('-');

        if (counts.TryGetValue(slug, out var count))
        {
            counts[slug] = count + 1;
            return $"{slug}-{count + 1}";
        }
        counts[slug] = 1; // First duplicate will be -2
        return slug;
    }
}