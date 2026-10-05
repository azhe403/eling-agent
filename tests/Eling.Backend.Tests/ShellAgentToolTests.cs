using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Agent.Services.Tools;
using Eling.Backend.Mcp;
using Eling.Core.FileSystem;
using Eling.Core.Scope;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// Behavioural tests for the <c>shell</c> agent tool. These really do spawn
/// processes: the point of the tool is that it runs a command line, so a mocked
/// process would assert nothing about argument escaping, exit codes or the
/// output cap.
///
/// Every command here is chosen to behave the same under cmd.exe and sh, so the
/// suite does not fork per-OS. The one exception is the timeout test, where the
/// two shells have no shared "sleep".
/// </summary>
public sealed class ShellAgentToolTests : IDisposable
{
    private const string ToolName = "shell";

    private readonly string _tempDir;
    private readonly ServiceProvider _provider;

    public ShellAgentToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-shell-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var services = new ServiceCollection();
        var userScope = UserScope.Resolve(Path.Combine(_tempDir, "user-scope"));
        var projectScope = new ProjectScope(_tempDir);
        var chain = new ScopeChain(_tempDir, [projectScope]);

        services.AddLogging();
        services.AddElingCoreServices(chain, userScope);
        services.AddElingAgentTools();

        _provider = services.BuildServiceProvider();
    }

    private IAgentTool ResolveTool()
    {
        using var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetServices<IAgentTool>().First(t => t.Name == ToolName);
    }

    private string WorkspaceRoot
    {
        get
        {
            using var scope = _provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<IFileSystemService>().WorkspaceRoot;
        }
    }

    private static string Args(string command, int? timeoutSeconds = null, int? maxOutputChars = null)
    {
        var parts = new Dictionary<string, object?> { ["command"] = command };
        if (timeoutSeconds.HasValue) parts["timeoutSeconds"] = timeoutSeconds.Value;
        if (maxOutputChars.HasValue) parts["maxOutputChars"] = maxOutputChars.Value;
        return JsonSerializer.Serialize(parts);
    }

    private static async Task<JsonElement> RunAsync(IAgentTool tool, string argumentsJson)
    {
        var output = await tool.ExecuteAsync(argumentsJson, CancellationToken.None);
        return JsonDocument.Parse(output).RootElement.Clone();
    }

    [Fact]
    public void Tool_AdvertisesAValidJsonSchemaWithCommandRequired()
    {
        var tool = ResolveTool();

        using var schema = JsonDocument.Parse(tool.ParametersJsonSchema);
        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        Assert.Contains("command", schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.False(string.IsNullOrWhiteSpace(tool.Description));
    }

    [Fact]
    public async Task Execute_RunsCommandAndCapturesStdout()
    {
        var result = await RunAsync(ResolveTool(), Args("echo hello-shell"));

        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Contains("hello-shell", result.GetProperty("stdout").GetString());
    }

    [Fact]
    public async Task Execute_NonZeroExit_IsReportedRatherThanThrown()
    {
        // git and most build tools signal failure with a non-zero exit; that is
        // a result the model must read, not an exception that kills the turn.
        var result = await RunAsync(ResolveTool(), Args("exit 3"));

        Assert.Equal(3, result.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Execute_CapturesStderrFromAFailingCommand()
    {
        var result = await RunAsync(ResolveTool(), Args("eling_no_such_command_zzz"));

        Assert.NotEqual(0, result.GetProperty("exitCode").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("stderr").GetString()));
    }

    [Fact]
    public async Task Execute_DefaultsWorkingDirectoryToTheWorkspaceRoot()
    {
        var result = await RunAsync(ResolveTool(), Args("cd"));

        Assert.Equal(
            WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            result.GetProperty("stdout").GetString()!.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public async Task Execute_HonoursAnExplicitWorkingDirectory()
    {
        var sub = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(sub);

        var result = await RunAsync(ResolveTool(),
            JsonSerializer.Serialize(new { command = "cd", workingDirectory = sub }));

        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Contains("nested", result.GetProperty("stdout").GetString());
    }

    [Fact]
    public async Task Execute_TruncatesOutputBeyondTheCap()
    {
        var noise = new string('x', 500);
        var result = await RunAsync(ResolveTool(), Args($"echo {noise}", maxOutputChars: 256));

        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.True(result.GetProperty("stdout").GetString()!.Length <= 256);
    }

    [Fact]
    public async Task Execute_ClampsACapBelowTheMinimumInsteadOfHonouringIt()
    {
        // A pathologically small cap would silently hide everything the model
        // asked for, so the tool clamps up to the documented floor rather than
        // obeying the request literally.
        var noise = new string('x', 500);
        var result = await RunAsync(ResolveTool(), Args($"echo {noise}", maxOutputChars: 64));

        Assert.True(result.GetProperty("stdout").GetString()!.Length > 64);
    }

    [Fact]
    public async Task Execute_EmptyCommand_ReturnsAnErrorInsteadOfThrowing()
    {
        var result = await RunAsync(ResolveTool(), Args("   "));

        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Execute_ReportsTimeoutAndDoesNotHangTheTurn()
    {
        // cmd has no sleep; ping against loopback is the portable stand-in.
        var sleeper = OperatingSystem.IsWindows()
            ? "ping -n 30 127.0.0.1 > nul"
            : "sleep 30";

        var result = await RunAsync(ResolveTool(), Args(sleeper, timeoutSeconds: 1));

        Assert.True(result.GetProperty("timedOut").GetBoolean());
    }

    public void Dispose()
    {
        _provider.Dispose();
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
