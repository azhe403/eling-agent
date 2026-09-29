using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Eling.Core.Codebase;

/// <summary>
/// Subset of <c>.gitignore</c> semantics honoring ignore files at the
/// workspace root and nested below it: blank lines and <c>#</c> comments are
/// skipped, <c>!</c> negates (last match wins), a trailing <c>/</c> means
/// directory-only (its contents are pruned wholesale), a leading <c>/</c> or
/// a middle slash anchors the pattern to the ignore file's directory,
/// otherwise the basename matches at any depth, and <c>*</c>/<c>?</c>/
/// <c>**</c> behave like git wildcards. Deeper ignore files override
/// shallower ones; an excluded directory is pruned before descending (so
/// negation below it cannot re-include, like git). Global excludes
/// (<c>~/.config/git/ignore</c>, <c>.git/info/exclude</c>) and trailing-space
/// escaping are intentionally out of scope. Edge: a file sharing an ignored
/// directory's exact name is skipped too.
/// </summary>
public sealed class GitignoreFilter
{
    private sealed record CompiledRule(
        Regex Pattern,
        bool Negation,
        int Depth,
        int Line,
        int BaseIndex);

    private readonly List<CompiledRule> _rules = [];
    private readonly List<string> _baseDirs = [];

    private GitignoreFilter()
    {
    }

    /// <summary>
    /// Load ignore rules from the workspace root: its <c>.gitignore</c> plus
    /// nested ones (never descending into indexer-excluded directories).
    /// Missing files mean allow-all; never throws for unreadable files.
    /// </summary>
    public static GitignoreFilter Load(string projectRoot, ILogger? logger = null)
    {
        var filter = new GitignoreFilter();
        var fullRoot = Path.GetFullPath(projectRoot);
        foreach (var file in DiscoverIgnoreFiles(fullRoot, logger))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A .gitignore we cannot read means its exclusions do not
                // apply — the files it would have kept out get indexed. Every
                // refusal reason counts here (locked, denied, an unreadable
                // path), so the catch stays wide; skipping the whole file is
                // the only safe response.
                logger?.LogWarning(ex, "Could not read ignore file {File}; its rules are not applied.", file);
                continue;
            }

            var relDir = Path.GetRelativePath(fullRoot, Path.GetDirectoryName(file)!).Replace('\\', '/');
            if (relDir == ".") relDir = "";
            var depth = relDir.Length == 0 ? 0 : relDir.Split('/').Length;
            var baseIndex = filter._baseDirs.Count;
            filter._baseDirs.Add(relDir);
            for (var i = 0; i < lines.Length; i++)
            {
                var compiled = Compile(lines[i], baseIndex, depth, i);
                if (compiled is not null) filter._rules.Add(compiled);
            }
        }

        // Deeper files override shallower ones; same-depth files cover
        // disjoint subtrees so their relative order is irrelevant.
        filter._rules.Sort(static (a, b) =>
            a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : a.Line.CompareTo(b.Line));

        return filter;
    }

    /// <summary>
    /// True when the workspace-relative path is ignored by the loaded rules.
    /// Accepts either slash style; a leading <c>./</c> is tolerated.
    /// </summary>
    public bool IsIgnored(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var rel = relativePath.Replace('\\', '/').Trim();
        if (rel.StartsWith("./", StringComparison.Ordinal)) rel = rel[2..];
        rel = rel.TrimStart('/');
        if (rel.Length == 0 || rel == ".." || rel.StartsWith("../", StringComparison.Ordinal)) return false;

        var ignored = false;
        foreach (var rule in _rules)
        {
            var baseDir = _baseDirs[rule.BaseIndex];
            string sub;
            if (baseDir.Length == 0)
            {
                sub = rel;
            }
            else if (rel.Equals(baseDir, StringComparison.Ordinal)
                || rel.StartsWith(baseDir + "/", StringComparison.Ordinal))
            {
                sub = rel.Length == baseDir.Length ? "" : rel[(baseDir.Length + 1)..];
            }
            else
            {
                continue;
            }

            if (sub.Length == 0) continue;
            if (rule.Pattern.IsMatch(sub)) ignored = !rule.Negation;
        }

        return ignored;
    }

    private static IEnumerable<string> DiscoverIgnoreFiles(string fullRoot, ILogger? logger = null)
    {
        var found = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(fullRoot);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            string[] subs = [], files = [];
            try { subs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
            catch (Exception ex)
            {
                // Nested ignore files under this directory go unseen, so their
                // exclusions silently stop applying.
                logger?.LogWarning(ex, "Could not enumerate {Dir} while looking for ignore files.", dir);
                continue;
            }
            foreach (var f in files)
            {
                if (string.Equals(Path.GetFileName(f), ".gitignore", StringComparison.Ordinal))
                    found.Add(f);
            }
            foreach (var s in subs)
            {
                var rel = Path.GetRelativePath(fullRoot, s).Replace('\\', '/');
                if (CodebaseIndexService.IsSkippedPath(rel)) continue;
                queue.Enqueue(s);
            }
        }

        return found;
    }

    private static CompiledRule? Compile(string line, int baseIndex, int depth, int lineNo)
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#')) return null;

        var negation = false;
        if (text.StartsWith('!'))
        {
            negation = true;
            text = text[1..];
        }
        else if (text.StartsWith("\\!", StringComparison.Ordinal) || text.StartsWith("\\#", StringComparison.Ordinal))
        {
            text = text[1..];
        }
        text = text.TrimEnd();
        if (text.Length == 0) return null;

        var dirOnly = text.EndsWith('/');
        if (dirOnly) text = text.TrimEnd('/');
        var anchored = text.StartsWith('/');
        if (anchored) text = text.TrimStart('/');
        if (text.Length == 0) return null;
        if (!anchored && text.Contains('/')) anchored = true;

        var body = TranslateWildcard(text);
        var tail = dirOnly ? "(/.*)?$" : "$";
        var full = anchored ? $"^{body}{tail}" : $"^(.*/)?{body}{tail}";
        return new CompiledRule(
            new Regex(full, RegexOptions.Compiled | RegexOptions.CultureInvariant),
            negation,
            depth,
            lineNo,
            baseIndex);
    }

    private static string TranslateWildcard(string pattern)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pattern.Length;)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                {
                    sb.Append("(.*/)?");
                    i += 3;
                }
                else
                {
                    sb.Append(".*");
                    i += 2;
                }
            }
            else if (pattern[i] == '*')
            {
                sb.Append("[^/]*");
                i++;
            }
            else if (pattern[i] == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else
            {
                sb.Append(Regex.Escape(pattern[i].ToString()));
                i++;
            }
        }

        return sb.ToString();
    }
}
