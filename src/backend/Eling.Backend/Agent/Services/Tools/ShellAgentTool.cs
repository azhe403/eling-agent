using System.Text;
using System.Text.Json;
using CliWrap;
using Eling.Backend.Agent.Ports;
using Eling.Core.FileSystem;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Agent.Services.Tools;

/// <summary>
/// Runs a command line on the agent's behalf: <c>git status</c>,
/// <c>dotnet build</c>, <c>rg</c>, anything the CLI already does better than a
/// bespoke tool would.
///
/// The command is handed to the platform shell (<c>cmd /c</c> or <c>sh -c</c>),
/// so pipes and redirection work the way the user expects. A non-zero exit is a
/// <em>result</em>, not an exception — <c>git diff --quiet</c> failing is
/// information the model needs, not a reason to abort the turn.
///
/// Two bounds protect the turn: the command is killed at a timeout, and
/// captured output is capped while it is read, so a runaway process cannot hang
/// the agent or exhaust memory. The working directory defaults to the
/// workspace root and is sandboxed to it unless the caller opts out.
/// </summary>
public sealed class ShellAgentTool(IFileSystemService fs, ILogger<ShellAgentTool> logger) : IAgentTool
{
    private const int DefaultTimeoutSeconds = 120;
    private const int MaxTimeoutSeconds = 600;
    private const int DefaultMaxOutputChars = 30_000;
    private const int MinMaxOutputChars = 256;
    private const int MaxMaxOutputChars = 200_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Absolute path to the shell. Resolved rather than left as a bare name:
    /// this process can be launched with a reduced environment (IDE, service
    /// host, MCP stdio child) where PATH resolution is not guaranteed.
    /// </summary>
    private static string ShellExecutable
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return "/bin/sh";
            }

            var comSpec = Environment.GetEnvironmentVariable("ComSpec");
            return string.IsNullOrWhiteSpace(comSpec) ? "cmd.exe" : comSpec;
        }
    }

    private static string ShellSwitch => OperatingSystem.IsWindows() ? "/c" : "-c";

    public string Name => "shell";

    public string Description =>
        "Run a shell command line in the workspace and return its exit code, stdout and stderr. "
        + "Use this for version control and tooling — git status, git log, dotnet build, rg — rather "
        + "than for reading or writing files, which have dedicated tools. Pipes and redirection work. "
        + "A non-zero exit is reported in the result, not raised as an error.";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "required": ["command"],
      "properties": {
        "command": { "type": "string", "description": "Command line to run, e.g. 'git status' or 'git log --oneline -20'." },
        "workingDirectory": { "type": "string", "description": "Directory to run in. Defaults to the workspace root." },
        "timeoutSeconds": { "type": "integer", "description": "Kill the command after this many seconds (1-600). Default 120." },
        "maxOutputChars": { "type": "integer", "description": "Cap on captured stdout/stderr, 256-200000. Default 30000." },
        "allowExternal": { "type": "boolean", "description": "Allow a workingDirectory outside the workspace root. Default false." }
      }
    }
    """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = doc.RootElement;

        var command = ReadString(root, "command").Trim();
        if (command.Length == 0)
        {
            return Error("command is required and must not be empty.");
        }

        var workspaceRoot = Path.GetFullPath(fs.WorkspaceRoot);
        var workingDirectory = workspaceRoot;
        var requestedDirectory = ReadString(root, "workingDirectory").Trim();

        if (requestedDirectory.Length > 0)
        {
            var candidate = Path.GetFullPath(requestedDirectory);
            if (!ReadBool(root, "allowExternal") && !IsUnderRoot(candidate, workspaceRoot))
            {
                return Error($"workingDirectory must be inside the workspace root ({workspaceRoot}) unless allowExternal is true.");
            }

            if (!Directory.Exists(candidate))
            {
                return Error($"workingDirectory does not exist: {candidate}");
            }

            workingDirectory = candidate;
        }

        var timeoutSeconds = Clamp(ReadInt(root, "timeoutSeconds", DefaultTimeoutSeconds), 1, MaxTimeoutSeconds);
        var cap = Clamp(ReadInt(root, "maxOutputChars", DefaultMaxOutputChars), MinMaxOutputChars, MaxMaxOutputChars);
        var outBuffer = new CappedBuffer(cap);
        var errBuffer = new CappedBuffer(cap);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var exitCode = -1;
        var timedOut = false;
        try
        {
            // CliWrap kills the whole process tree when the token fires, so a
            // command that spawns children does not outlive its timeout.
            var result = await Cli.Wrap(ShellExecutable)
                .WithArguments([ShellSwitch, command])
                .WithWorkingDirectory(workingDirectory)
                .WithStandardOutputPipe(PipeTarget.ToDelegate(outBuffer.AppendLine))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(errBuffer.AppendLine))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(timeoutCts.Token);

            exitCode = result.ExitCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Shell command could not be started: {Command}", command);
            return Error($"could not run command: {ex.Message}");
        }

        return JsonSerializer.Serialize(
            new
            {
                command,
                workingDirectory,
                exitCode,
                timedOut,
                truncated = outBuffer.Truncated || errBuffer.Truncated,
                stdout = outBuffer.Text,
                stderr = errBuffer.Text
            },
            JsonOptions);
    }

    private static string Error(string message)
        => JsonSerializer.Serialize(new { error = message }, JsonOptions);

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static bool ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.True;

    private static int ReadInt(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out var element) && element.TryGetInt32(out var value) ? value : fallback;

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private static bool IsUnderRoot(string candidate, string root)
    {
        var separator = Path.DirectorySeparatorChar;
        var normalizedRoot = root.TrimEnd(separator, Path.AltDirectorySeparatorChar) + separator;
        var normalizedCandidate = candidate.TrimEnd(separator, Path.AltDirectorySeparatorChar) + separator;
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collects piped output up to a cap, enforcing the cap as the stream is
    /// read rather than after: a runaway command must not be able to fill memory
    /// before the limit is applied.
    /// </summary>
    private sealed class CappedBuffer(int cap)
    {
        private readonly StringBuilder _builder = new();
        private readonly object _gate = new();

        public bool Truncated { get; private set; }

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _builder.ToString();
                }
            }
        }

        public void AppendLine(string line)
        {
            lock (_gate)
            {
                var newLineLength = Environment.NewLine.Length;
                if (_builder.Length >= cap)
                {
                    Truncated = true;
                    return;
                }

                var remaining = cap - _builder.Length;
                if (remaining >= line.Length + newLineLength)
                {
                    _builder.Append(line).Append(Environment.NewLine);
                    return;
                }

                var room = Math.Max(0, Math.Min(line.Length, remaining - newLineLength));
                if (room > 0)
                {
                    _builder.Append(line.AsSpan(0, room));
                }

                Truncated = true;
            }
        }
    }
}
