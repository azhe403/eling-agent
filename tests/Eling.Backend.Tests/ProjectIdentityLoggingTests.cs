using System.Text;
using Eling.Backend.Bootstrap;

namespace Eling.Backend.Tests;

/// <summary>
/// The project identity in the log is the folder the host opened, not the
/// <c>.eling</c> scope that owns the memories.
/// </summary>
/// <remarks>
/// The failure this pins: the identity was taken from the scope root, so a
/// service opened inside an umbrella scope logged under the umbrella's name.
/// Every sibling service under one scope then produced the same identity, and
/// the log disagreed with the dashboard, which keys workspaces by
/// <c>workspaceRoot</c>.
/// <para>
/// The two facts are separate and both must survive the fix. The identity is
/// the opened folder; the scope root is the directory that owns the memories,
/// and the effective data dir follows the scope, not the identity. This test
/// reads the REAL startup line the MCP host writes, so reverting the call site
/// to the scope root fails here even though the unit-level helper still
/// passes.
/// </para>
/// </remarks>
[Collection(ElingDataDirCollection.Name)]
public sealed class ProjectIdentityLoggingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalDataDir;
    private readonly string _originalUserScope;

    public ProjectIdentityLoggingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-projectid-" + Guid.NewGuid().ToString("N")[..8]);
        _originalDataDir = Environment.GetEnvironmentVariable("ELING_DATA_DIR") ?? string.Empty;
        _originalUserScope = Environment.GetEnvironmentVariable("ELING_USER_SCOPE") ?? string.Empty;
    }

    /// <summary>
    /// The workspace is opened inside a scope that owns the memories: the
    /// umbrella directory holds <c>.eling</c>, the service folder under it does
    /// not, and the service folder is what gets opened.
    /// </summary>
    [Fact]
    public void Startup_LogsTheOpenedFolderAsIdentity_AndTheScopeRootAsScope()
    {
        var scopeRoot = Path.Combine(_tempDir, "iron-bird");
        var opened = Path.Combine(scopeRoot, "execution-order", "execution-order");
        Directory.CreateDirectory(Path.Combine(scopeRoot, ".eling"));
        Directory.CreateDirectory(opened);

        var content = BuildHostAndReadStartupLine(opened);

        // The identity is the opened folder, not the scope that owns the memories.
        Assert.Contains("[project:execution-order]", content);
        Assert.DoesNotContain("[project:iron-bird]", content);

        // The scope root is a different fact and stays visible: it is where the
        // memories actually live.
        Assert.Contains($"Scope root: {scopeRoot}", content);
        Assert.Contains($"Effective data dir: {Path.Combine(scopeRoot, ".eling")}", content);
    }

    /// <summary>
    /// A workspace with no scope anywhere has no scope root, and the log says
    /// so instead of printing the workspace as if it were a scope.
    /// </summary>
    [Fact]
    public void Startup_ReportsNoScopeRoot_WhenTheWorkspaceHasNoScope()
    {
        var opened = Path.Combine(_tempDir, "fresh", "execution-order");
        Directory.CreateDirectory(opened);

        var content = BuildHostAndReadStartupLine(opened);

        Assert.Contains("[project:execution-order]", content);
        Assert.Contains("Scope root: (none)", content);
    }

    /// <summary>
    /// Builds the MCP host the way production does — the context is resolved
    /// from the opened folder — then reads back the startup line it wrote to
    /// the central log directory.
    /// </summary>
    /// <remarks>
    /// The opened folder is passed to the context resolver rather than set as
    /// the process working directory. Changing the process directory is
    /// process-global, and the test suites that spawn real backends run in
    /// parallel with this one: a child process that inherited a directory this
    /// test later deleted would fail for reasons that have nothing to do with
    /// the log identity.
    /// </remarks>
    private string BuildHostAndReadStartupLine(string opened)
    {
        var dataDir = Path.Combine(_tempDir, "data");
        Environment.SetEnvironmentVariable("ELING_DATA_DIR", dataDir);
        Environment.SetEnvironmentVariable("ELING_USER_SCOPE", Path.Combine(_tempDir, "user-scope"));

        using (McpHostBuilder.Build(ProjectContext.Discover(opened), dashboardPort: 0))
        {
            var logPath = Path.Combine(dataDir, "logs", "backend.log");
            Assert.True(File.Exists(logPath), $"the host did not write {logPath}");
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ELING_DATA_DIR", _originalDataDir.Length == 0 ? null : _originalDataDir);
        Environment.SetEnvironmentVariable("ELING_USER_SCOPE", _originalUserScope.Length == 0 ? null : _originalUserScope);
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
