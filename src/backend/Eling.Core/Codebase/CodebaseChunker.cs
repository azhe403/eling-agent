namespace Eling.Core.Codebase;

/// <summary>
/// Splits a file into retrieval chunks that do not cut a declaration in half.
/// </summary>
/// <remarks>
/// Chunks pack whole members up to <see cref="TargetLines"/>. A member larger
/// than <see cref="MaxChunkLines"/> cannot be kept whole, so it is split and the
/// tail is repeated as overlap — the one case where chunks are not a partition
/// of the file. A file with no detectable declarations (JSON, a shell script, a
/// source file whose braces do not balance) falls back to fixed windows.
/// </remarks>
public static class CodebaseChunker
{
    /// <summary>Line count a chunk aims for before it looks for a boundary.</summary>
    public const int TargetLines = 280;

    /// <summary>Hard cap. A forced cut never produces a chunk longer than this.</summary>
    public const int MaxChunkLines = 420;

    /// <summary>No chunk is shorter than this, so a boundary is never taken too eagerly.</summary>
    public const int MinChunkLines = 140;

    /// <summary>Stop this far short of the target, so a boundary sits inside the chunk.</summary>
    public const int HeadroomLines = 25;

    /// <summary>Lines repeated when a member had to be split across chunks.</summary>
    public const int OverlapLines = 30;

    private static readonly HashSet<string> BraceLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".java", ".go", ".rs",
    };

    public static IReadOnlyList<CodebaseChunk> Chunk(string filePath, string content)
    {
        if (string.IsNullOrEmpty(content)) return [];

        var lines = content.Split('\n');
        var boundaries = BraceLanguages.Contains(Path.GetExtension(filePath))
            ? DeclarationScanner.FindMemberBoundaries(lines)
            : FindProseBoundaries(lines);

        var chunks = new List<CodebaseChunk>();
        var cursor = 0;
        while (cursor < lines.Length)
        {
            var goal = Math.Min(cursor + TargetLines, lines.Length);
            if (goal >= lines.Length)
            {
                chunks.Add(Slice(lines, filePath, cursor, lines.Length));
                break;
            }

            var cut = boundaries.Length == 0 ? -1 : FindBoundary(boundaries, cursor, goal);
            if (cut < 0 && boundaries.Length == 0)
            {
                chunks.Add(Slice(lines, filePath, cursor, goal));
                cursor = goal;
                continue;
            }

            // Nothing in reach: this member is bigger than a chunk. Cut at the cap
            // and repeat the tail so its continuation is reachable from either side.
            var forced = cut < 0;
            if (forced) cut = Math.Min(goal, cursor + MaxChunkLines);
            chunks.Add(Slice(lines, filePath, cursor, cut));
            cursor = forced ? Math.Max(cursor + 1, cut - OverlapLines) : cut;
        }
        return chunks;
    }

    /// <summary>
    /// The last boundary that leaves at least <see cref="MinChunkLines"/> in the
    /// chunk and keeps <see cref="HeadroomLines"/> clear of the target, or -1.
    /// </summary>
    private static int FindBoundary(int[] boundaries, int cursor, int goal)
    {
        var found = -1;
        foreach (var boundary in boundaries)
        {
            if (boundary <= cursor + MinChunkLines) continue;
            if (boundary > goal - HeadroomLines) break;
            found = boundary;
        }
        return found;
    }

    /// <summary>
    /// Prose has no braces to read, so a section heading is the boundary. A blank
    /// line is not: the blank under a heading is followed by body text, and
    /// cutting there would start the next chunk mid-section.
    /// </summary>
    private static int[] FindProseBoundaries(string[] lines)
    {
        var boundaries = new List<int>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith('#')) boundaries.Add(i);
        }
        return [.. boundaries];
    }

    private static CodebaseChunk Slice(string[] lines, string filePath, int start, int end)
        => new(filePath, start + 1, end, string.Join('\n', lines[start..end]));
}