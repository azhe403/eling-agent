using Eling.Backend.Bootstrap;
using Eling.Core;
using Eling.Core.Scope;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// Temporal workspaces (chat sessions, scratch checkouts) must not
/// participate in codebase indexing: no DB, no watcher, no dropdown or
/// federation entry. Opt-out is explicit via <c>ELING_CODEBASE_EXCLUDE</c>.
/// </summary>
public sealed class CodebaseExclusionTests : IDisposable
{
    private const string ExcludeEnv = "ELING_CODEBASE_EXCLUDE";

    private readonly string _tempDir;
    private readonly string? _originalExclude;

    public CodebaseExclusionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-excl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _originalExclude = Environment.GetEnvironmentVariable(ExcludeEnv);
    }

    [Fact]
    public void IsCodebaseExcluded_EmptyEnv_AllowsEverything()
    {
        Environment.SetEnvironmentVariable(ExcludeEnv, null);

        Assert.False(ElingPaths.IsCodebaseExcluded(_tempDir));
    }

    [Fact]
    public void IsCodebaseExcluded_MatchesRootAndChildrenOnly()
    {
        var chats = Path.Combine(_tempDir, "chats");
        Directory.CreateDirectory(Path.Combine(chats, "session-1"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "work"));
        Environment.SetEnvironmentVariable(ExcludeEnv, chats);

        Assert.True(ElingPaths.IsCodebaseExcluded(chats));
        Assert.True(ElingPaths.IsCodebaseExcluded(Path.Combine(chats, "session-1")));
        Assert.False(ElingPaths.IsCodebaseExcluded(Path.Combine(_tempDir, "work")));
        Assert.False(ElingPaths.IsCodebaseExcluded(_tempDir));
    }

    [Fact]
    public void IsCodebaseExcluded_SupportsMultipleRoots()
    {
        var a = Path.Combine(_tempDir, "a");
        var b = Path.Combine(_tempDir, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        Environment.SetEnvironmentVariable(ExcludeEnv, string.Join(Path.PathSeparator, [a, b]));

        Assert.True(ElingPaths.IsCodebaseExcluded(a));
        Assert.True(ElingPaths.IsCodebaseExcluded(b));
        Assert.False(ElingPaths.IsCodebaseExcluded(_tempDir));
    }

    [Fact]
    public void IsCodebaseExcluded_DefaultChatsRoot_ExcludedWithoutEnv()
    {
        Environment.SetEnvironmentVariable(ExcludeEnv, null);
        var home = Path.Combine(_tempDir, "home");
        var session = Path.Combine(home, ".config", "openchamber", "chats", "session-9");
        Directory.CreateDirectory(session);
        Directory.CreateDirectory(Path.Combine(home, "work"));

        Assert.True(ElingPaths.IsCodebaseExcluded(session, excludeRoots: null, userHome: home));
        Assert.True(ElingPaths.IsCodebaseExcluded(Path.Combine(home, ".config", "opencode"), excludeRoots: null, userHome: home));
        Assert.True(ElingPaths.IsCodebaseExcluded(Path.Combine(home, ".config", "opencode", "skills"), excludeRoots: null, userHome: home));
        Assert.False(ElingPaths.IsCodebaseExcluded(Path.Combine(home, "work"), excludeRoots: null, userHome: home));
    }

    [Fact]
    public void RuntimeRegistration_ExcludedWorkspace_DisablesCodebaseOnly()
    {
        var chats = Path.Combine(_tempDir, "chats");
        var session = Path.Combine(chats, "session-1");
        Directory.CreateDirectory(Path.Combine(chats, ".eling"));
        Directory.CreateDirectory(session);
        Environment.SetEnvironmentVariable(ExcludeEnv, chats);

        var chain = new ScopeChain(session, [new ProjectScope(chats)]);
        var userScope = UserScope.Resolve(Path.Combine(_tempDir, "user-scope"));
        var context = new ProjectContext(chain, userScope, Path.Combine(chats, ".eling"), false);

        var reg = RuntimeSelfRegistration.Build(context);

        Assert.False(reg.CodebaseEnabled);
        Assert.Equal(Path.GetFullPath(chats), reg.HeadScopeRoot);
    }

    [Fact]
    public void RuntimeRegistration_NonExcludedWorkspace_StaysEnabled()
    {
        Environment.SetEnvironmentVariable(ExcludeEnv, Path.Combine(_tempDir, "elsewhere"));

        var chain = new ScopeChain(_tempDir, [new ProjectScope(_tempDir)]);
        var userScope = UserScope.Resolve(Path.Combine(_tempDir, "user-scope"));
        var context = new ProjectContext(chain, userScope, Path.Combine(_tempDir, ".eling"), false);

        var reg = RuntimeSelfRegistration.Build(context);

        Assert.True(reg.CodebaseEnabled);
    }

    [Fact]
    public void IsCodebaseExcluded_MalformedEntry_ThrowsInsteadOfSkipping()
    {
        // A malformed entry must fail loudly. Skipping it silently would drop
        // the exclusion the user asked for and index the very workspace
        // ELING_CODEBASE_EXCLUDE exists to keep out.
        //
        // Passed as a parameter rather than through the environment: setting an
        // environment variable truncates the value at the NUL, so the entry
        // would arrive as "bad" and parse cleanly.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ElingPaths.IsCodebaseExcluded(_tempDir, excludeRoots: "bad\0entry"));

        Assert.Contains("bad\0entry", ex.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ExcludeEnv, _originalExclude);
        // Best-effort teardown: a leftover temp dir must never fail an otherwise
        // passing run. A locked or still-in-use file is the only realistic
        // failure, and there is nothing useful to report once the test is over.
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
