using Eling.Backend.Bootstrap;
using Eling.Core;

namespace Eling.Backend.Tests;

/// <summary>
/// Scope-chain consent rule: discovering an uninitialized project must never
/// create a `.eling` directory under the working directory. Only an explicit
/// user-approved `memory_init_project` may create one.
/// </summary>
public sealed class ProjectContextNoAutoCreateTests : IDisposable
{
    private const string UserScopeEnv = "ELING_USER_SCOPE";

    private readonly string _userRoot;
    private readonly string _cwd;
    private readonly string _originalCwd;
    private readonly string? _originalUserScope;

    public ProjectContextNoAutoCreateTests()
    {
        _userRoot = Path.Combine(Path.GetTempPath(), "eling-dummy-user-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_userRoot);
        _cwd = Path.Combine(Path.GetTempPath(), "eling-dummy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_cwd);
        _originalCwd = Directory.GetCurrentDirectory();
        _originalUserScope = Environment.GetEnvironmentVariable(UserScopeEnv);
        Environment.SetEnvironmentVariable(UserScopeEnv, _userRoot);
        Directory.SetCurrentDirectory(_cwd);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        Environment.SetEnvironmentVariable(UserScopeEnv, _originalUserScope);
        try { Directory.Delete(_userRoot, recursive: true); } catch { }
        try { Directory.Delete(_cwd, recursive: true); } catch { }
    }

    [Fact]
    public void Discover_NoElingAnywhere_DoesNotCreateDotEling()
    {
        var context = ProjectContext.Discover();

        Assert.False(context.Chain.IsInitialized);
        Assert.False(Directory.Exists(Path.Combine(_cwd, ProjectScope.DataDirectoryName)));
    }
}