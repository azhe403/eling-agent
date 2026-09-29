using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace Eling.Backend.Tests;

/// <summary>
/// Guards the project rule that external commands go through CliWrap rather
/// than raw <c>Process.Start</c> / <c>ProcessStartInfo</c> (see AGENTS.md).
///
/// The allowlist is closed. Every entry is a pre-existing carve-out with a
/// stated reason; adding a row here is the same mistake as extending the
/// tuple allowlist in <see cref="CodeOrganizationConventionTests"/> — convert
/// the call site to CliWrap instead.
/// </summary>
public sealed class CliWrapConventionTests
{
    /// <summary>
    /// Bare <c>Process.Start</c>. The lookahead keeps <c>Process.StartTime</c>
    /// (a property read in <c>Bootstrap/ProcessStartTime.cs</c>) from matching.
    /// </summary>
    private static readonly Regex[] ForbiddenPatterns =
    [
        new(@"Process\.Start(?!Time)", RegexOptions.Compiled),
        new(@"ProcessStartInfo", RegexOptions.Compiled),
    ];

    // Grandfathered ProcessStartInfo usage, relative to the repo root with
    // forward slashes. Do NOT extend — migrate the call site to CliWrap.
    //
    // FrontendDevSpawner is file-granular rather than line-granular: the same
    // file both uses CliWrap correctly (TrySpawnPnpmFrontend) and holds one
    // documented probe carve-out (GetPidsListeningOnPort). Splitting the file
    // would buy a finer allowlist, which is not worth the churn.
    private static readonly HashSet<string> ProcessStartInfoAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "src/desktop/Eling.Desktop/Services/BackendSupervisor.cs",
        "src/backend/Eling.Backend/Bootstrap/FrontendDevSpawner.cs",
        "tests/Eling.Backend.Tests/TestProcesses.cs",
        "tests/Eling.Backend.Tests/MemoryProjectToolsTests.cs",
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

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
            || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
            || path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}");

    private static string Relative(string repoRoot, string fullPath)
        => Path.GetRelativePath(repoRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Drops the comment portion of a line. Line comments are cut at the first
    /// <c>//</c>; block-comment bodies start with <c>*</c> and are dropped whole.
    /// A <c>//</c> inside a string literal (a URL) also truncates the line, which
    /// can only cause a missed match on that one line, never a false positive.
    /// </summary>
    private static string StripComment(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("/*", StringComparison.Ordinal))
            return string.Empty;
        if (trimmed.StartsWith('*'))
            return string.Empty;

        var commentStart = line.IndexOf("//", StringComparison.Ordinal);
        return commentStart < 0 ? line : line[..commentStart];
    }

    [Fact]
    public void ExternalCommands_DoNotUseProcessStart_BeyondAllowlist()
    {
        var repoRoot = RepoRoot();
        var offenders = new List<string>();

        foreach (var relativeRoot in new[] { "src", "tests" })
        {
            var root = Path.Combine(repoRoot, relativeRoot);
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // This file names the forbidden patterns as string literals, so
                // scanning it would report itself.
                if (Path.GetFileName(file).Equals("CliWrapConventionTests.cs", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsBuildOutput(file))
                    continue;

                var rel = Relative(repoRoot, file);
                if (ProcessStartInfoAllowlist.Contains(rel))
                    continue;

                var lineNo = 0;
                foreach (var rawLine in File.ReadLines(file))
                {
                    lineNo++;

                    // Match code, not prose. XML doc comments that name the
                    // forbidden API would otherwise flag themselves, and a
                    // trailing "// ... Process.Start" note is not a call site.
                    var line = StripComment(rawLine);
                    if (line.Length == 0)
                        continue;

                    if (ForbiddenPatterns.Any(p => p.IsMatch(line)))
                        offenders.Add($"{rel}:{lineNo}: {rawLine.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "External commands must use CliWrap (Cli.Wrap), not Process.Start / ProcessStartInfo. "
            + "Do not extend the allowlist — convert the call site instead:\n"
            + string.Join("\n", offenders));
    }
}
