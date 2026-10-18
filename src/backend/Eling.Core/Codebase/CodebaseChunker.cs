namespace Eling.Core.Codebase;

/// <summary>
/// Splits a file into retrieval chunks that do not cut a declaration in half.
/// </summary>
/// <remarks>
/// For brace-delimited languages, chunk boundaries are taken from
/// <see cref="DeclarationScanner"/>: every member (method, property, ...) becomes
/// its own chunk, with the file preamble (usings, namespace, class header)
/// attached to the first member. A member larger than
/// <see cref="MaxChunkLines"/> cannot stay whole, so it is split and the tail is
/// repeated as overlap — the one case where chunks are not a partition of the
/// file. Files with no detectable declarations (unbalanced braces, non-brace
/// languages, prose) fall back to the packed-window strategy (or fixed windows
/// when no boundaries exist).
/// </remarks>
public static class CodebaseChunker
{
    /// <summary>Line count a chunk aims for before it looks for a boundary (packed mode).</summary>
    public const int TargetLines = 280;

    /// <summary>Hard cap. A forced cut never produces a chunk longer than this.</summary>
    public const int MaxChunkLines = 420;

    /// <summary>No chunk is shorter than this, so a boundary is never taken too eagerly (packed mode).</summary>
    public const int MinChunkLines = 140;

    /// <summary>Stop this far short of the target, so a boundary sits inside the chunk (packed mode).</summary>
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
        var ext = Path.GetExtension(filePath);

        if (BraceLanguages.Contains(ext))
        {
            var raw = DeclarationScanner.FindMemberBoundaries(lines);
            // Drop container declarations (class/struct/interface/record/enum/namespace):
            // they are not members and must not become their own tiny chunk; their
            // header travels as the preamble of the first real member.
            var boundaries = raw.Where(b => !IsContainerDeclaration(lines[b])).ToArray();
            if (boundaries.Length == 0)
            {
                return PackedChunks(lines, filePath, []);
            }
            return MemberChunks(lines, filePath, boundaries);
        }

        var proseBoundaries = FindProseBoundaries(lines);
        return PackedChunks(lines, filePath, proseBoundaries);
    }

    /// <summary>
    /// One chunk per member. The preamble (everything before the first member)
    /// travels with the first member so imports/context stay attached.
    /// </summary>
    private static List<CodebaseChunk> MemberChunks(string[] lines, string filePath, int[] boundaries)
    {
        var chunks = new List<CodebaseChunk>();
        for (var i = 0; i < boundaries.Length; i++)
        {
            var memberStart = boundaries[i];
            var memberEnd = (i + 1 < boundaries.Length) ? boundaries[i + 1] : lines.Length;
            // Attach the file preamble to the first member only.
            var chunkStart = (i == 0) ? 0 : memberStart;
            var span = memberEnd - chunkStart;

            if (span <= MaxChunkLines)
            {
                chunks.Add(Slice(lines, filePath, chunkStart, memberEnd));
                continue;
            }

            // Oversized member: split with tail overlap.
            var cursor = chunkStart;
            while (cursor < memberEnd)
            {
                var cut = Math.Min(cursor + MaxChunkLines, memberEnd);
                if (cut >= memberEnd)
                {
                    chunks.Add(Slice(lines, filePath, cursor, memberEnd));
                    break;
                }
                chunks.Add(Slice(lines, filePath, cursor, cut));
                cursor = Math.Max(cursor + 1, cut - OverlapLines);
            }
        }
        return chunks;
    }

    /// <summary>
    /// Packed-window strategy: pack whole members until <see cref="TargetLines"/>,
    /// cutting at the last reachable boundary. Used for prose and as the fallback
    /// for unbalanced/non-brace sources.
    /// </summary>
    private static List<CodebaseChunk> PackedChunks(string[] lines, string filePath, int[] boundaries)
    {
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

    private static bool IsContainerDeclaration(string line)
    {
        var trimmed = line.TrimStart();
        // Strip leading modifiers to reach the type keyword.
        var words = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var w in words)
        {
            if (w is "class" or "struct" or "interface" or "record" or "enum" or "namespace")
                return true;
            // Stop scanning modifiers once we hit a non-modifier token.
            if (w is not ("public" or "private" or "protected" or "internal" or "static"
                or "sealed" or "abstract" or "partial" or "readonly" or "unsafe"
                or "file" or "ref"))
                break;
        }
        return false;
    }

    private static CodebaseChunk Slice(string[] lines, string filePath, int start, int end)
        => new(filePath, start + 1, end, string.Join('\n', lines[start..end]));
}
