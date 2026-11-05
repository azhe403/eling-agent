using Eling.Backend.Bootstrap;
using Eling.Core;
using Eling.Core.Scope;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// The MCP host died on a workspace whose appsettings.json was not valid JSON.
/// The generic host loads appsettings.json from the content root, and the MCP
/// host's content root is the workspace directory, so a malformed file in the
/// workspace the user merely opened threw during <c>builder.Build()</c> —
///
///   System.IO.InvalidDataException: Failed to load configuration from file
///   '...\appsettings.json'.
///    ---> System.FormatException: Could not parse the JSON file.
///    ---> System.Text.Json.JsonReaderException
///
/// The exception escaped <c>Program.Main</c>, so the MCP server never started
/// and every tool call failed. The HTTP host never had the problem: it clears
/// the configuration sources and points its content root at the binary's own
/// directory (see <see cref="HttpLoopConfigurationIsolationTests"/>).
///
/// Eling reads no configuration keys at all (there is no IConfiguration key
/// read anywhere under src/backend), so the workspace's appsettings.json
/// contributes nothing the product needs — it can only break the host.
/// These tests pin that the MCP host starts regardless of what the
/// workspace's appsettings files contain.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class McpHostConfigurationIsolationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalCwd;

    public McpHostConfigurationIsolationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-mcpcfg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _originalCwd = Environment.CurrentDirectory;
    }

    /// <summary>
    /// The failure itself: a malformed appsettings.json in the workspace must
    /// not throw out of the host build. This is the regression test for the
    /// crash; it fails with InvalidDataException before the fix.
    /// </summary>
    [Fact]
    public void McpHost_Starts_WithMalformedAppSettingsInTheWorkspace()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"), "{ \"ConnectionStrings\": { \"Default\": \"Server=x;\"");

        var exception = Record.Exception(() => BuildMcpHost());

        Assert.Null(exception);
    }

    /// <summary>
    /// The same for an empty file, and for a file whose JSON is broken in a
    /// different way (an unquoted key). The JSON configuration provider
    /// accepts comments and trailing commas, so those are not the trigger —
    /// malformed JSON in general is.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("{ not json at all }")]
    [InlineData("{ \"Unclosed\": { \"Key\": 1 ")]
    [InlineData("{ UnquotedKey: 1 }")]
    [InlineData("{ 'SingleQuoted': 'x' }")]
    public void McpHost_Starts_WithBrokenAppSettingsContent(string content)
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"), content);

        var exception = Record.Exception(() => BuildMcpHost());

        Assert.Null(exception);
    }

    /// <summary>
    /// The workspace appsettings must not reach the configuration at all. A
    /// well-formed file that carries a value is still not configuration Eling
    /// asked for: it would be a silent way for a workspace to steer the host.
    /// </summary>
    [Fact]
    public void McpHost_DoesNotLoadAppSettingsFromTheWorkspace()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.json"), """
        {
          "CustomWorkspaceKey": "leaked_value"
        }
        """);

        using var host = BuildMcpHost();

        var config = host.Services.GetRequiredService<IConfiguration>();
        Assert.Null(config["CustomWorkspaceKey"]);
    }

    /// <summary>
    /// Environment variables must survive the isolation: the host still reads
    /// them, and they are the supported way to configure a run.
    /// </summary>
    [Fact]
    public void McpHost_StillReadsEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("ELING_TEST_CONFIG_PROBE", "from-env");
        try
        {
            using var host = BuildMcpHost();

            var config = host.Services.GetRequiredService<IConfiguration>();
            Assert.Equal("from-env", config["ELING_TEST_CONFIG_PROBE"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELING_TEST_CONFIG_PROBE", null);
        }
    }

    /// <summary>
    /// Builds the MCP host the way <c>Program.Main</c> does: the workspace is
    /// the process working directory, and the generic host takes its content
    /// root from there — which is exactly how a malformed workspace
    /// appsettings.json reached the configuration. The user scope stays
    /// isolated the way <see cref="HttpLoopConfigurationIsolationTests"/> does,
    /// so the build never touches the developer's real scopes.
    /// </summary>
    private IHost BuildMcpHost()
    {
        Environment.CurrentDirectory = _tempDir;
        var chain = ScopeChain.Discover(_tempDir);
        var userScope = UserScope.Resolve(Path.Combine(_tempDir, "user_scope"));
        var context = new ProjectContext(
            chain,
            userScope,
            Path.Combine(_tempDir, ".eling"),
            IsUserHome: false);
        return McpHostBuilder.Build(context, dashboardPort: 0);
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _originalCwd;
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
