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

    /// <summary>
    /// Standing rule (user, 2026-09-30): EVERY catch block logs, in any form,
    /// at any layer — an empty catch, or one that only carries a comment, is a
    /// swallowed failure and is never acceptable.
    /// </summary>
    /// <remarks>
    /// Heuristic detector: finds each <c>catch</c> keyword, skips an optional
    /// parameter and <c>when</c> filter, brace-matches to the closing brace,
    /// strips comments and whitespace, and reports a body with nothing left.
    /// String contents (including <c>"""raw</c> strings and characters like
    /// '}' or '//' inside them) are never stripped, so a catch whose body is
    /// code never reports. A catch whose body holds only a raw string with
    /// braces would misclose early — that is a false negative, which is the
    /// safe direction for this check. Keep literal "catch {" out of strings;
    /// reword such strings instead.
    /// </remarks>
    [Fact]
    public void CatchBlocks_MustNotBeSilent()
    {
        var repoRoot = RepoRoot();
        var byFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Sources())
        {
            // This file is the detector; its own literals spell out "catch {" shapes.
            if (file.EndsWith("CodeOrganizationConventionTests.cs", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Relative(repoRoot, file);
            var n = SilentCatches(File.ReadAllText(file), rel).Count();
            if (n > 0) byFile[rel] = n;
        }

        var offenders = new List<string>();
        foreach (var (rel, count) in byFile.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (SilentCatchAllowlist.TryGetValue(rel, out var allowed))
            {
                if (count > allowed)
                    offenders.Add($"{rel}: {count} silent catch(s), allowlist permits {allowed} (list is shrink-only, never extend)");
                else if (count < allowed)
                    offenders.Add($"{rel}: silent-catch count dropped to {count} - lower/remove its allowlist entry (shrink-only)");
            }
            else
            {
                offenders.Add($"{rel}: {count} silent catch(s) — silent catches are never acceptable");
            }
        }
        foreach (var rel in SilentCatchAllowlist.Keys)
        {
            if (!byFile.ContainsKey(rel))
                offenders.Add($"{rel}: allowlist entry no longer matches any silent catch - remove it (shrink-only)");
        }
        Assert.True(offenders.Count == 0,
            "Every catch block must log, at any layer - no empty or comment-only bodies:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// Baseline of silent catches present on 2026-11-12 (178 sites, 118 files,
    /// harvested from the whole-tree scan; fix these as you touch each file).
    /// SHRINK-ONLY: a PR may reduce a count but may never raise one, and a
    /// file that reaches zero must have its entry removed — the test fails
    /// either way rather than silently blessing a new site.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> SilentCatchAllowlist =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/backend/Eling.Backend/AuditDirWatcher.cs"] = 1,
            ["src/backend/Eling.Backend/Bootstrap/CodebaseWatcherService.cs"] = 2,
            ["src/backend/Eling.Backend/Bootstrap/DashboardPort.cs"] = 1,
            ["src/backend/Eling.Backend/Bootstrap/FrontendDevSpawner.cs"] = 1,
            ["src/backend/Eling.Backend/Bootstrap/HttpLoop.cs"] = 1,
            ["src/backend/Eling.Backend/Bootstrap/RuntimeSelfRegistration.cs"] = 1,
            ["src/backend/Eling.Backend/Bootstrap/ZombieProcessCleaner.cs"] = 1,
            ["src/backend/Eling.Backend/CodebaseDirWatcher.cs"] = 1,
            ["src/backend/Eling.Backend/Controllers/CoordinatorController.cs"] = 5,
            ["src/backend/Eling.Backend/DatabaseRegistryWatcher.cs"] = 1,
            ["src/backend/Eling.Backend/HttpCoordinatorMemoryChangeNotifier.cs"] = 1,
            ["src/backend/Eling.Backend/Logging/LogFormatSwitcher.cs"] = 2,
            ["src/backend/Eling.Backend/Mcp/DisposableAuditLogger.cs"] = 1,
            ["src/backend/Eling.Backend/Mcp/Tools/MemoryReadTool.cs"] = 1,
            ["src/backend/Eling.Backend/Mcp/Tools/MemoryWriteTool.cs"] = 1,
            ["src/backend/Eling.Backend/RuntimeDirWatcher.cs"] = 1,
            ["src/backend/Eling.Backend/RuntimeRegistry.cs"] = 6,
            ["src/backend/Eling.Backend/Services/RuntimeMetricsBroadcaster.cs"] = 2,
            ["src/backend/Eling.Backend/Updates/UpdateCheckerService.cs"] = 2,
            ["src/backend/Eling.Core/Audit/Storage/BufferedAuditLogger.cs"] = 4,
            ["src/backend/Eling.Core/Audit/Storage/SqliteAuditIndex.cs"] = 2,
            ["src/backend/Eling.Core/Database/AppSettingsConnectionScanner.cs"] = 3,
            ["src/backend/Eling.Core/Database/JsonDatabaseConnectionStore.cs"] = 1,
            ["src/backend/Eling.Core/Database/RedisConnectionPool.cs"] = 5,
            ["src/backend/Eling.Core/Memory/ScopedMemoryService.cs"] = 2,
            ["src/backend/Eling.Core/Projects/JsonWorkspaceRegistry.cs"] = 1,
            ["src/backend/Eling.Core/Scope/CanonicalProjectRoot.cs"] = 1,
            ["src/backend/Eling.Core/Scope/CentralAgentDirectory.cs"] = 2,
            ["src/backend/Eling.Core/Scope/CentralAuditDirectory.cs"] = 1,
            ["src/backend/Eling.Core/Scope/CentralLogDirectory.cs"] = 1,
            ["src/backend/Eling.Core/Workspaces/WorkspaceExpansionStore.cs"] = 1,
            ["src/desktop/Eling.Desktop/Models/MessageBlock.cs"] = 1,
            ["src/desktop/Eling.Desktop/Services/BackendSupervisor.cs"] = 1,
            ["src/desktop/Eling.Desktop/Services/ElingApiClient.cs"] = 1,
            ["src/desktop/Eling.Desktop/ViewModels/FoldersViewModel.cs"] = 1,
            ["src/desktop/Eling.Desktop/ViewModels/MainViewModel.cs"] = 1,
            ["src/desktop/Eling.Desktop/Views/MainWindow.axaml.cs"] = 1,
            ["tests/Eling.Backend.Tests/AgentBuiltinMcpToolTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/AgentChatStreamTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/AgentChatTests.cs"] = 2,
            ["tests/Eling.Backend.Tests/AgentHostBrowseTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/AgentStoreMigrationTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/AgentTurnPromptCompositionTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/AgentWorkspaceTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/CodebaseExclusionTests.cs"] = 4,
            ["tests/Eling.Backend.Tests/CodebaseRebuildScopeTests.cs"] = 2,
            ["tests/Eling.Backend.Tests/CodebaseStaleSweepTests.cs"] = 2,
            ["tests/Eling.Backend.Tests/CodebaseUninitializedIndexTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/CodebaseWorkspaceRootTests.cs"] = 4,
            ["tests/Eling.Backend.Tests/DashboardLifecycleTests.cs"] = 3,
            ["tests/Eling.Backend.Tests/DatabaseDetectImportTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/DatabaseRegistryWatcherTests.cs"] = 3,
            ["tests/Eling.Backend.Tests/Endpoints/StatsEndpointsTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/FileSystem/FileSystemServiceTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/HttpLoopConfigurationIsolationTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/JsonProjectScopePolicyStoreTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/JudgeEndpointsTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/Logging/LogConfigStoreTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/Logging/LogFormatForwardingSinkTests.cs"] = 2,
            ["tests/Eling.Backend.Tests/Logging/LogFormatSwitcherTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/Logging/RollingDailyFileSinkJsonlTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/LoggingTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/Mcp/ToolPolicyFilterTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/Mcp/ToolsPolicyToolTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/McpHostConfigurationIsolationTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/McpProcessTests.cs"] = 2,
            ["tests/Eling.Backend.Tests/MeaiGatewayTests.cs"] = 2,
            ["tests/Eling.Backend.Tests/MemoryApiTests.cs"] = 2,
            ["tests/Eling.Backend.Tests/MemoryMaintenanceApiTests.cs"] = 3,
            ["tests/Eling.Backend.Tests/MemoryProjectScopePolicyToolTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/MemoryProjectToolsTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/MemoryProvenanceDtoTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/MemoryRecallToolCodebaseTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/MemoryRecallToolOverflowSummaryTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/MemoryRecallToolProjectScopeTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/MemoryRecallToolTopicAliasTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/MemoryWriteToolProjectScopeTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/Pecut10VerificationTests.cs"] = 2,
            ["tests/Eling.Backend.Tests/PeerMemoryNotifyTests.cs"] = 2,
            ["tests/Eling.Backend.Tests/ProjectContextNoAutoCreateTests.cs"] = 2,
            ["tests/Eling.Backend.Tests/ProjectIdentityLoggingTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/ProjectMemoryChainEndpointTests.cs"] = 2,
            ["tests/Eling.Backend.Tests/ProjectScopeCatalogTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/RuntimeDirWatcherTests.cs"] = 2,
            ["tests/Eling.Backend.Tests/RuntimeMetricsSamplerTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/RuntimeRegistryWorkspacePruneTests.cs"] = 3,
            ["tests/Eling.Backend.Tests/RuntimeRegistryWorkspaceRecordingTests.cs"] = 3,
            ["tests/Eling.Backend.Tests/ScopeChainDiTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/ScopeChainSmokeTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/SemanticJudgeStoreTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/Shell/ShellConfigStoreTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/Shell/ShellToolsTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/ShellAgentToolTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/TestProcesses.cs"] = 5,
            ["tests/Eling.Backend.Tests/Tools/ToolPolicyStoreTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/Updates/FileUpdateCacheTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/Updates/UpdateCheckerTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/Updates/UpdateNoticeBlockTests.cs"] = 1,
            ["tests/Eling.Backend.Tests/ZombieProcessCleanerTests.cs"] = 2,
            ["tests/Eling.Core.Tests/AppSettingsConnectionScannerTests.cs"] = 1,
            ["tests/Eling.Core.Tests/AppSettingsSourceResolverTests.cs"] = 1,
            ["tests/Eling.Core.Tests/CacheFileGateTests.cs"] = 1,
            ["tests/Eling.Core.Tests/CodebaseIndexSizeTests.cs"] = 2,
            ["tests/Eling.Core.Tests/CodebaseSiblingReadOnlyFirstTests.cs"] = 1,
            ["tests/Eling.Core.Tests/DatabaseReadServiceCacheTests.cs"] = 1,
            ["tests/Eling.Core.Tests/JsonDatabaseReadCacheTests.cs"] = 1,
            ["tests/Eling.Core.Tests/JsonRedisReadCacheTests.cs"] = 1,
            ["tests/Eling.Core.Tests/JsonWorkspaceRegistryTests.cs"] = 1,
            ["tests/Eling.Core.Tests/LogReaderJsonlTests.cs"] = 1,
            ["tests/Eling.Core.Tests/LogReaderTests.cs"] = 1,
            ["tests/Eling.Core.Tests/ProjectScopeDiscoverChainTests.cs"] = 1,
            ["tests/Eling.Core.Tests/ProjectScopeTests.cs"] = 1,
            ["tests/Eling.Core.Tests/RedisKeyReadServiceOfflineTests.cs"] = 1,
            ["tests/Eling.Core.Tests/RedisScanCommandTests.cs"] = 1,
            ["tests/Eling.Core.Tests/ScopeChainTests.cs"] = 1,
            ["tests/Eling.Core.Tests/TempWorkspace.cs"] = 2,
            ["tests/Eling.Core.Tests/UserScopeTests.cs"] = 1,
        };


    private static IEnumerable<string> SilentCatches(string text, string rel)
    {
        const string keyword = "catch";
        var index = 0;
        while ((index = text.IndexOf(keyword, index, StringComparison.Ordinal)) >= 0)
        {
            var at = index;
            index += keyword.Length;

            var cursor = SkipWhitespaceAndComments(text, index);
            if (cursor < text.Length && text[cursor] == '(')
            {
                var close = MatchClosing(text, cursor, '(', ')');
                if (close < 0) continue;
                cursor = SkipWhitespaceAndComments(text, close + 1);
            }
            if (HasWordAt(text, cursor, "when"))
            {
                cursor = SkipWhitespaceAndComments(text, cursor + 4);
                if (cursor < text.Length && text[cursor] == '(')
                {
                    var close = MatchClosing(text, cursor, '(', ')');
                    if (close < 0) continue;
                    cursor = SkipWhitespaceAndComments(text, close + 1);
                }
            }
            if (cursor >= text.Length || text[cursor] != '{') continue;

            var open = cursor;
            var closeBrace = MatchClosing(text, open, '{', '}');
            if (closeBrace < 0) continue;

            if (IsEffectivelyEmpty(text.Substring(open + 1, closeBrace - open - 1)))
            {
                var lineNo = 1 + text.AsSpan(0, at).Count('\n');
                yield return $"{rel}:{lineNo}: empty or comment-only catch body";
            }
        }
    }

    /// <summary>Advance past whitespace and comments (both kinds).</summary>
    private static int SkipWhitespaceAndComments(string text, int start)
    {
        var i = start;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
            {
                var nl = text.IndexOf('\n', i);
                i = nl < 0 ? text.Length : nl + 1;
                continue;
            }
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }
            break;
        }
        return i;
    }

    /// <summary>
    /// Brace/paren matching that is string- and comment-aware, so a bracket
    /// inside a string or comment never shifts the balance.
    /// </summary>
    private static int MatchClosing(string text, int openAt, char open, char close)
    {
        var depth = 0;
        for (var i = openAt; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == open) { depth++; continue; }
            if (ch == close)
            {
                depth--;
                if (depth == 0) return i;
                continue;
            }
            if (ch == '"' || ch == '\'')
            {
                i = SkipString(text, i, ch);
                continue;
            }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var nl = text.IndexOf('\n', i);
                i = nl < 0 ? text.Length : nl;
                continue;
            }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return -1;
                i = end + 1;
                continue;
            }
        }
        return -1;
    }

    /// <summary>Skip a string/char literal; escapes are honoured.</summary>
    private static int SkipString(string text, int start, char quote)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == quote) return i;
        }
        return text.Length - 1;
    }

    /// <summary>True when the body reduces to nothing once strings can hold content and comments/whitespace are removed.</summary>
    private static bool IsEffectivelyEmpty(string body)
    {
        var i = 0;
        while (i < body.Length)
        {
            var ch = body[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '/' && i + 1 < body.Length && body[i + 1] == '/')
            {
                var nl = body.IndexOf('\n', i);
                i = nl < 0 ? body.Length : nl + 1;
                continue;
            }
            if (ch == '/' && i + 1 < body.Length && body[i + 1] == '*')
            {
                var end = body.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return false; // unterminated comment is still code-shaped; leave it to the compiler
                i = end + 2;
                continue;
            }
            // Anything else — code, or the contents of a string — counts as present.
            return false;
        }
        return true;
    }

    private static bool HasWordAt(string text, int index, string word)
    {
        if (index + word.Length > text.Length) return false;
        if (string.CompareOrdinal(text, index, word, 0, word.Length) != 0) return false;
        var after = index + word.Length;
        return after >= text.Length || char.IsWhiteSpace(text[after]) || text[after] == '(';
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
