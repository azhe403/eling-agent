namespace Eling.Core.Codebase;

/// <summary>
/// Finds the lines in a brace-delimited source file where a new declaration
/// begins, so a chunk can end there instead of cutting a member in half.
/// </summary>
/// <remarks>
/// A boundary is reported where a block that was opened on a DECLARATION line
/// has just closed. Control-flow blocks are excluded on purpose: a cut after an
/// <c>if</c> block lands in the middle of the method that contains it, which is
/// the failure this scanner exists to prevent.
/// </remarks>
public static class DeclarationScanner
{
    // Openers that are control flow rather than declaration. A block opened by
    // one of these is nested inside a member, so closing it is not a boundary.
    private static readonly HashSet<string> ControlFlowOpeners = new(StringComparer.Ordinal)
    {
        "case", "catch", "do", "else", "finally", "fixed", "for", "foreach", "if",
        "lock", "return", "switch", "try", "unchecked", "using", "while", "yield",
    };

    /// <summary>
    /// 0-based line indices at which a declaration starts, ascending. Empty when
    /// the file does not parse as balanced — the caller then falls back to plain
    /// line windows rather than trusting a scan that went wrong.
    /// </summary>
    public static int[] FindMemberBoundaries(string[] lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var openers = new Stack<int>();
        var declarationStarts = new List<int>();
        var depth = 0;
        var inBlockComment = false;
        var inString = false;
        var verbatimString = false;
        var escaped = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            for (var j = 0; j < line.Length; j++)
            {
                var c = line[j];
                if (inBlockComment)
                {
                    if (c == '*' && j + 1 < line.Length && line[j + 1] == '/') { inBlockComment = false; j++; }
                    continue;
                }
                if (inString)
                {
                    if (verbatimString)
                    {
                        if (c != '"') continue;
                        if (j + 1 < line.Length && line[j + 1] == '"') { j++; continue; }
                        inString = false;
                        continue;
                    }
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c is '"' or '\'') inString = false;
                    continue;
                }
                if (c == '/' && j + 1 < line.Length)
                {
                    if (line[j + 1] == '/') break;
                    if (line[j + 1] == '*') { inBlockComment = true; j++; continue; }
                }
                if (c is '"' or '\'')
                {
                    verbatimString = c == '"' && j > 0 && line[j - 1] == '@';
                    inString = true;
                    continue;
                }
                if (c == '`') { SkipTemplateLiteral(line, ref j); continue; }
                if (c == '{') { openers.Push(i); depth++; continue; }
                if (c != '}') continue;

                depth--;
                if (openers.Count == 0) continue;
                var declaration = DeclarationLineAbove(lines, openers.Pop());
                if (declaration >= 0) declarationStarts.Add(declaration);
            }
        }

        if (depth != 0 || inBlockComment || inString) return [];

        var boundaries = new List<int>(declarationStarts.Count);
        foreach (var start in declarationStarts)
        {
            // Walk up over doc comments and attributes: they describe the member
            // below them and must travel with it, not stay behind.
            var boundary = start;
            while (boundary > 0 && IsMemberTrivia(lines[boundary - 1])) boundary--;
            if (boundary > 0 && !boundaries.Contains(boundary)) boundaries.Add(boundary);
        }
        boundaries.Sort();
        return [.. boundaries];
    }

    /// <summary>
    /// The declaration a block belongs to, or -1 when the block is control flow.
    /// The brace line itself is rarely the signature — most styles put the brace
    /// on its own line — so the search steps up past blanks, attributes and
    /// comments to the first line that says something.
    /// </summary>
    private static int DeclarationLineAbove(string[] lines, int braceLine)
    {
        var trimmedBrace = lines[braceLine].TrimStart();
        if (trimmedBrace.StartsWith('}') || ControlFlowOpeners.Contains(FirstWord(trimmedBrace)))
        {
            return -1;
        }

        for (var i = braceLine - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]) || IsMemberTrivia(lines[i])) continue;
            return IsDeclarationLine(lines[i]) ? i : -1;
        }
        return -1;
    }

    private static void SkipTemplateLiteral(string line, ref int j)
    {
        for (j++; j < line.Length; j++)
        {
            if (line[j] == '\\') { j++; continue; }
            if (line[j] == '`') return;
        }
    }

    private static bool IsDeclarationLine(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0) return false;
        if (trimmed[0] is '{' or '}' or ')' or ']' or ',') return false;
        if (trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith('*')) return false;
        return !ControlFlowOpeners.Contains(FirstWord(trimmed));
    }

    /// <summary>Attributes and doc comments belonging to the member that follows.</summary>
    private static bool IsMemberTrivia(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith('[') || trimmed.StartsWith("//", StringComparison.Ordinal);
    }

    private static string FirstWord(string trimmed)
    {
        var end = 0;
        while (end < trimmed.Length && (char.IsLetter(trimmed[end]) || trimmed[end] == '_')) end++;
        return end == 0 ? string.Empty : trimmed[..end];
    }
}