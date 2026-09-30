using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace Eling.Backend.Tests;

/// <summary>
/// Guards the agreed code-organization conventions for the backend:
/// one top-level type per file (records grouped per area are the only
/// exception), multi-parameter records chopped one-param-per-line, and
/// MCP tool files holding exactly the tool class.
/// </summary>
public sealed class CodeOrganizationConventionTests
{
    private static readonly Regex TypeDecl = new(
        @"^(?:public|internal)\s+(?:\w+\s+)*(class|record|struct|enum|interface)\s+(\w+)",
        RegexOptions.Compiled);

    // Record declarations — `record`, `record struct`, `record class` — behind
    // any accessibility, modifier or attribute, and not necessarily at column
    // zero. The keyword is matched as a whole word rather than read off the
    // type-keyword group in TypeDecl: on `public readonly record struct Foo` that
    // group captures `struct`, so testing it for equality with "record" skipped
    // every record struct in the backend.
    private static readonly Regex RecordDecl = new(
        @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|file|static|sealed|abstract|readonly|partial|new)\s+)*record\b\s*(?:(?:struct|class)\s+)?(\w+)",
        RegexOptions.Compiled);

    /// <summary>
    /// How many parameters the primary-constructor list holds when that whole
    /// list sits on the declaration line, or -1 when it does not — a record
    /// already chopped one-parameter-per-line leaves its <c>(</c> unclosed
    /// here. Search starts past the type name so an attribute's own
    /// parentheses, as in <c>[Obsolete("a", "b")]</c>, cannot be mistaken for
    /// the parameter list. Commas nested in brackets, parentheses, braces or a
    /// string do not begin a new parameter, so one parameter with a collection
    /// default is not miscounted.
    /// </summary>
    private static int ParameterCountOnLine(string line, int searchFrom)
    {
        var open = line.IndexOf('(', searchFrom);
        if (open < 0) return -1;

        var depth = 0;
        var commas = 0;
        var quote = '\0';
        for (var i = open; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                // Both escape forms matter: a backslash escape ("C:\\" must not
                // read as a closing quote) and a doubled quote, which is how a
                // verbatim string spells an embedded quote.
                if (c == '\\' && i + 1 < line.Length) { i++; continue; }
                if (c == quote)
                {
                    if (i + 1 < line.Length && line[i + 1] == quote) { i++; continue; }
                    quote = '\0';
                }
                continue;
            }

            if (c is '"' or '\'') { quote = c; continue; }

            if (c is '(' or '[' or '{') { depth++; continue; }
            if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0) return commas + 1;
                continue;
            }

            if (c == ',' && depth == 1) commas++;
        }

        return -1;
    }

    // Tuple (ValueTuple) shapes: Task<(…)> returns, deconstruction
    // declarations (var (a, b) = …), deconstruction assignments
    // ((a, b) = await …, excluding == and =>), tuple literal returns,
    // and tuple literal assignments (excluding compound assignments like
    // +=, comparisons, and parenthesized await/new/call expressions).
    // Named records replace all of these.
    private static readonly Regex[] TuplePatterns =
    [
        new(@"(Task|IReadOnlyList|IReadOnlyCollection|IList|List|Dictionary|Array|Enumerable|Func|Action)<\s*\(",
            RegexOptions.Compiled),
        new(@"var\s+\(", RegexOptions.Compiled),
        new(@"\)\s*=(?![=>])", RegexOptions.Compiled),
        new(@"return\s+\([^)]*,", RegexOptions.Compiled),
        new(@"(?<![+\-*/%&|^!<>?=])=\s*\(\s*(?!(await|new)\b|\w+\s*\()[^)]*,", RegexOptions.Compiled),
    ];

    // Files grandfathered with tuple shapes, relative to the repo root with
    // forward slashes. Memory/filesystem/runtime plumbing predates the
    // no-tuple rule. Do NOT extend this list — declare a named record
    // instead, then remove the file from the list.
    private static readonly HashSet<string> TupleAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "src/backend/Eling.Core/Memory/Storage/SqliteMemoryIndex.cs",
        "src/backend/Eling.Core/Memory/MemoryService.cs",
        "src/backend/Eling.Core/Memory/ScopedMemoryService.cs",
        "src/backend/Eling.Core/MemoryRecall/IntentionTriggerMatcher.cs",
        "src/backend/Eling.Core/MemoryRecall/MemoryRecallService.cs",
        "src/backend/Eling.Core/Logging/RollingDailyFileSink.cs",
        "src/backend/Eling.Backend/RuntimeRegistry.cs",
        "src/backend/Eling.Backend/FileSystem/FileSystemService.cs",
        "src/backend/Eling.Backend/Agent/Services/BackendFileTools.cs",
        "src/backend/Eling.Backend/Agent/Infrastructure/Ai/MeaiChatGateway.cs",
        "src/backend/Eling.Backend/Endpoints/AgentChatEndpoints.cs",
        "src/backend/Eling.Backend/Endpoints/AgentWorkspaceEndpoints.cs",
    };

    // The per-area exception AGENTS.md grants, expressed as patterns rather
    // than filenames. Records grouped per area are allowed to share a file, so
    // a new Dtos/ or *Models.cs file conforms without needing an entry here —
    // listing them by name would make every future one another allowlist edit.
    private static readonly string[] MultiTypePatterns =
    [
        "*/Dtos/*",
        "*Models.cs",
    ];

    // Files grandfathered with more than one top-level type for a reason other
    // than the per-area exception, relative to the repo root with forward
    // slashes — legacy to split later. Do NOT extend this list — split the
    // file instead.
    private static readonly HashSet<string> MultiTypeAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "src/backend/Eling.Backend/Agent/Services/TurnStreamEvents.cs",
        "src/backend/Eling.Backend/Agent/Ports/AgentMessages.cs",
        "src/backend/Eling.Backend/Agent/Services/BackendChatStore.cs",
        "src/backend/Eling.Backend/Agent/Ports/IProviderClient.cs",
        "src/backend/Eling.Backend/Dtos/SaveMemoryResponse.cs",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Eling.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Repo root (Eling.slnx) not found from test output.");
    }

    private static IReadOnlyList<string> BackendSources()
    {
        var root = Path.Combine(RepoRoot(), "src", "backend");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
    }

    private static string Relative(string repoRoot, string fullPath)
        => Path.GetRelativePath(repoRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    [Fact]
    public void McpToolFiles_ContainExactlyOneTopLevelType()
    {
        var repoRoot = RepoRoot();
        var offenders = new List<string>();
        foreach (var file in BackendSources().Where(f =>
                     f.Contains($"{Path.DirectorySeparatorChar}Mcp{Path.DirectorySeparatorChar}Tools{Path.DirectorySeparatorChar}")))
        {
            var count = File.ReadLines(file).Count(l => TypeDecl.IsMatch(l));
            if (count != 1)
                offenders.Add($"{Relative(repoRoot, file)} has {count} top-level types");
        }
        Assert.True(offenders.Count == 0,
            "MCP tool files must hold exactly one top-level type:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void MultiParameterRecords_AreChoppedOnePerLine()
    {
        var repoRoot = RepoRoot();
        var offenders = new List<string>();
        foreach (var file in BackendSources())
        {
            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                var m = RecordDecl.Match(line);
                if (!m.Success) continue;
                // More than one parameter sharing the declaration line is the
                // violation. Counting parameters rather than searching for a
                // comma anywhere on the line is what keeps `= [1, 2]` out of
                // the offender list, and what makes the two-parameter case —
                // a single separator — fail the "> 1" test rather than slip
                // past it.
                if (ParameterCountOnLine(line, m.Index + m.Length) > 1)
                    offenders.Add($"{Relative(repoRoot, file)}:{lineNo}: {line.Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Multi-parameter records must be chopped one-param-per-line:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void NoNewMultiTypeFiles_BeyondAllowlist()
    {
        var repoRoot = RepoRoot();
        var offenders = new List<string>();
        foreach (var file in BackendSources())
        {
            var count = File.ReadLines(file).Count(l => TypeDecl.IsMatch(l));
            if (count <= 1) continue;

            var rel = Relative(repoRoot, file);
            var allowed = MultiTypeAllowlist.Contains(rel)
                || MultiTypePatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, rel, ignoreCase: true));
            if (!allowed)
                offenders.Add($"{rel} has {count} top-level types");
        }
        Assert.True(offenders.Count == 0,
            "New files must hold one top-level type (split the file instead of extending the allowlist):\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void NoTuples_BeyondAllowlist()
    {
        var repoRoot = RepoRoot();
        var offenders = new List<string>();
        foreach (var file in BackendSources())
        {
            var rel = Relative(repoRoot, file);
            if (TupleAllowlist.Contains(rel))
                continue;
            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                if (TuplePatterns.Any(p => p.IsMatch(line)))
                    offenders.Add($"{rel}:{lineNo}: {line.Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Backend code must use named records instead of tuples (do not extend the allowlist):\n"
            + string.Join("\n", offenders));
    }
}
