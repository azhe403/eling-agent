using Eling.Backend.Bootstrap;
using Eling.Core;
using Eling.Core.Scope;

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
    private readonly string? _originalUserScope;

    public ProjectContextNoAutoCreateTests()
    {
        _userRoot = Path.Combine(Path.GetTempPath(), "eling-dummy-user-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_userRoot);
        _cwd = Path.Combine(Path.GetTempPath(), "eling-dummy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_cwd);
        _originalUserScope = Environment.GetEnvironmentVariable(UserScopeEnv);
        Environment.SetEnvironmentVariable(UserScopeEnv, _userRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(UserScopeEnv, _originalUserScope);
        try { Directory.Delete(_userRoot, recursive: true); } catch { }
        try { Directory.Delete(_cwd, recursive: true); } catch { }
    }

    [Fact]
    public void Discover_NoElingAnywhere_DoesNotCreateDotEling()
    {
        // Start directory passed explicitly: mutating the process current
        // directory here races with parallel test classes that resolve it
        // (e.g. WebApplication.CreateBuilder's file watcher) and crashes them
        // when this fixture deletes its temp directory mid-run.
        var context = ProjectContext.Discover(_cwd);

        Assert.False(context.Chain.IsInitialized);
        Assert.False(Directory.Exists(Path.Combine(_cwd, ProjectScope.DataDirectoryName)));
    }
}