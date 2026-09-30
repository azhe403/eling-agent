using System.Text.RegularExpressions;

namespace Eling.Backend.Tests;

/// <summary>
/// Guards the agreed code-organization conventions: one top-level type per file,
/// with a single exception for a small group of related DTO/model types, records
/// chopped one-parameter-per-line, no tuples, and MCP tool files holding exactly
/// the tool class.
/// </summary>
/// <remarks>
/// The grouping rule is keyed on what the types ARE, never on the folder they sit
/// in. Keying on the folder let a <c>Dtos/</c> file of two plain classes pass while
/// would have failed the desktop's <c>Models/*Dtos.cs</c> files, which are exactly
/// the shape the rule allows. Both allowlists below are self-cleaning: an entry that
/// no longer describes a real multi-type file fails the suite, so a split file cannot
/// leave a silent exemption behind.
/// </remarks>
public sealed class CodeOrganizationConventionTests
{
    /// <summary>How large a file may be before grouping several DTO/model types in it stops being allowed.</summary>
    private const int MaxGroupedTypeFileLines = 100;

    // Top-level type declarations only: anchored at column zero so a nested or
    // indented type never counts. `record` is matched before `struct`/`class` so
    // `public readonly record struct Foo` is read as a record, and the optional
    // trailing `struct`/`class` is consumed before the name.
    private static readonly Regex TopLevelType = new(
        @"^(?:public|internal|file)\s+(?:(?:sealed|abstract|static|partial|readonly|unsafe|new)\s+)*(?<kind>record|class|struct|enum|interface)\s+(?:(?:struct|class)\s+)?(?<name>\w+)",
        RegexOptions.Compiled);

    // Same declaration, but any accessibility, any leading attribute, and not
    // necessarily at column zero — used by the record-chopping check, which must
    // see indented records too.
    private static readonly Regex RecordDecl = new(
        @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|file|static|sealed|abstract|readonly|partial|new)\s+)*record\b\s*(?:(?:struct|class)\s+)?(\w+)",
        RegexOptions.Compiled);

    // A type declaration whose base list names INotifyPropertyChanged. Anchored on
    // the base list and stopped at the body brace, so a cast or a local in
    // expression position is not mistaken for declaring the interface.
    private static readonly Regex InheritsChangeNotification = new(
        @"^\s*(?:\[[^\]]*\]\s*)*(?:public|internal|private|protected|file|static|sealed|abstract|partial|readonly|new)\s+[^{]*:\s*[^{]*\bINotifyPropertyChanged\b",
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

    // Files grandfathered with more than one top-level type, relative to the repo
    // root with forward slashes. Legacy to split later. Do NOT extend this list —
    // split the file instead. NoNewMultiTypeAllowlistEntries asserts every entry
    // still describes a real multi-type file, so a split file cannot leave a
    // silent exemption behind.
    private static readonly HashSet<string> MultiTypeAllowlist = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Every hand-written C# file in the solution, build output excluded.</summary>
    private static IReadOnlyList<string> Sources()
    {
        var results = new List<string>();
        foreach (var dir in new[] { Path.Combine("src", "backend"), Path.Combine("src", "desktop"), "tests" })
        {
            var root = Path.Combine(RepoRoot(), dir);
            if (!Directory.Exists(root)) continue;
            results.AddRange(Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")));
        }
        return results;
    }

    /// <summary>
    /// Backend sources only. The no-tuple check is deliberately not widened: its
    /// last pattern leans on a negative lookahead to exclude a call from the
    /// parens, which is easy to misread as a positive alternative. Widening it is
    /// worth doing, but only alongside a test that pins the pattern against a
    /// parenthesised call so the exclusion cannot silently invert.
    /// </summary>
    private static IReadOnlyList<string> BackendSources()
        => [.. Sources().Where(p => p.Contains($"{Path.DirectorySeparatorChar}backend{Path.DirectorySeparatorChar}"))];

    private static string Relative(string repoRoot, string fullPath)
        => Path.GetRelativePath(repoRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static List<string> TopLevelTypes(IEnumerable<string> lines)
        => [.. lines.Where(l => TopLevelType.IsMatch(l)).Select(l => l.Trim())];

    private static bool IsInterface(string declaration) => TopLevelType.Match(declaration).Groups["kind"].Value == "interface";

    private static bool IsModel(string declaration)
    {
        var kind = TopLevelType.Match(declaration).Groups["kind"].Value;
        return kind is "record" or "enum";
    }

    [Fact]
    public void McpToolFiles_ContainExactlyOneTopLevelType()
    {
        var repoRoot = RepoRoot();
        var offenders = new List<string>();
        foreach (var file in Sources().Where(f =>
                     f.Contains($"{Path.DirectorySeparatorChar}Mcp{Path.DirectorySeparatorChar}Tools{Path.DirectorySeparatorChar}")))
        {
            var count = TopLevelTypes(File.ReadLines(file)).Count;
            if (count != 1)
                offenders.Add($"{Relative(repoRoot, file)} has {count} top-level types");
        }
        Assert.True(offenders.Count == 0,
            "MCP tool files must hold exactly one top-level type:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void MultiTypeFiles_AreOnlySmallDtoGroups()
    {
        var repoRoot = RepoRoot();
        var offenders = new List<string>();
        foreach (var file in Sources())
        {
            var rel = Relative(repoRoot, file);
            if (MultiTypeAllowlist.Contains(rel))
                continue;

            var lines = File.ReadAllLines(file);
            var types = TopLevelTypes(lines);
            if (types.Count <= 1)
                continue;

            // An interface is a contract, not a model. It never shares a file.
            if (types.Any(IsInterface))
                offenders.Add($"{rel}: an interface may not share a file with other types");

            // The one permitted grouping: related DTO/model types, and only
            // while the file stays small enough to still read as one subject.
            var notModels = types.Where(t => !IsModel(t)).ToList();
            if (notModels.Count > 0)
                offenders.Add($"{rel}: non-model type(s) sharing the file: {string.Join(" | ", notModels)}");

            if (lines.Length >= MaxGroupedTypeFileLines)
                offenders.Add($"{rel}: {lines.Length} lines, over the {MaxGroupedTypeFileLines}-line limit for a grouped file");
        }
        Assert.True(offenders.Count == 0,
            $"Grouping several types is allowed only for a DTO/model group under {MaxGroupedTypeFileLines} lines, and never with an interface. Split the file instead:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// Folder responsibility, enforced by name because that is the one property a
    /// test can check without an exemption list to maintain: a folder of view
    /// models holds view models. Presentation rows, session state and tree nodes
    /// are models, and they used to sit here, which is how non-view code ends up
    /// in the view layer. Deliberately not a dependency rule on top — checking that
    /// Models/ never touches ReactiveUI or INotifyPropertyChanged would need an
    /// exemption for the presentation rows that legitimately raise change
    /// notifications, and an exemption is exactly the thing that rots.
    /// </summary>
    [Fact]
    public void ViewModelsFolder_HoldsOnlyViewModels()
    {
        var desktop = Path.Combine(RepoRoot(), "src", "desktop", "Eling.Desktop", "ViewModels");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories))
        {
            foreach (var declaration in TopLevelTypes(File.ReadLines(file)))
            {
                var name = TopLevelType.Match(declaration).Groups["name"].Value;
                if (name.EndsWith("ViewModel", StringComparison.Ordinal)
                    || name == "ViewModelBase")
                {
                    continue;
                }

                offenders.Add($"{Relative(RepoRoot(), file)}: {name} is not a view model — move it to Models/");
            }
        }
        Assert.True(offenders.Count == 0,
            "ViewModels/ holds only view models; a row, session or node is a model:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// Views/ is for views. A hand-written control is view-layer code but is not
    /// a view, so it belongs beside Converters/ in its own folder — the pattern
    /// this repo already established for the four IValueConverters. Checked by
    /// file extension, so there is no allowlist to maintain.
    /// </summary>
    [Fact]
    public void ViewsFolder_HoldsOnlyXamlAndItsCodeBehind()
    {
        var views = Path.Combine(RepoRoot(), "src", "desktop", "Eling.Desktop", "Views");
        var offenders = Directory.EnumerateFiles(views, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f))
            .Where(n => !n.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                     && !n.EndsWith(".axaml.cs", StringComparison.OrdinalIgnoreCase))
            .Select(n => $"  Views/{n} is not a view — move a hand-written control to Controls/")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Views/ holds only .axaml and its .axaml.cs code-behind:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// The desktop project references ReactiveUI, so change notification goes
    /// through <c>ReactiveObject</c> rather than a hand-rolled
    /// <c>INotifyPropertyChanged</c>: two ways to raise the same event is how the
    /// two drifted apart in the first place. Scoped to the desktop because the
    /// ASP.NET backend must not take a ReactiveUI dependency. Anchored on the base
    /// list, so subscribing through the interface — which is legal, since
    /// ReactiveObject implements it — is not mistaken for declaring it.
    /// </summary>
    [Fact]
    public void DesktopTypes_DoNotHandRollChangeNotification()
    {
        var desktop = Path.Combine(RepoRoot(), "src", "desktop", "Eling.Desktop");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                if (InheritsChangeNotification.IsMatch(line))
                    offenders.Add($"  {Relative(RepoRoot(), file)}:{lineNo}: {line.Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Derive from ReactiveObject instead of declaring INotifyPropertyChanged:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void NoNewMultiTypeAllowlistEntries_AndNoStaleOnes()
    {
        var repoRoot = RepoRoot();
        var problems = new List<string>();
        foreach (var entry in MultiTypeAllowlist)
        {
            var path = Path.Combine(repoRoot, entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                problems.Add($"{entry}: allowlisted but the file no longer exists — remove the entry");
                continue;
            }

            if (TopLevelTypes(File.ReadAllLines(path)).Count <= 1)
                problems.Add($"{entry}: holds one type or fewer, so it no longer needs an exemption — remove the entry");
        }
        Assert.True(problems.Count == 0,
            "The multi-type allowlist must not accumulate dead entries:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void NoStaleTupleAllowlistEntries()
    {
        var repoRoot = RepoRoot();
        var stale = new List<string>();
        foreach (var entry in TupleAllowlist)
        {
            var path = Path.Combine(repoRoot, entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                stale.Add($"{entry}: allowlisted but the file no longer exists — remove the entry");
        }
        Assert.True(stale.Count == 0,
            "The tuple allowlist must not accumulate dead entries:\n" + string.Join("\n", stale));
    }

    [Fact]
    public void MultiParameterRecords_AreChoppedOnePerLine()
    {
        var repoRoot = RepoRoot();
        var offenders = new List<string>();
        foreach (var file in Sources())
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
            "Code must use named records instead of tuples (do not extend the allowlist):\n"
            + string.Join("\n", offenders));
    }
}
