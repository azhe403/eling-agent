using System.Text.Json.Serialization;

namespace Eling.Core.Shell;

/// <summary>
/// Global configuration of the <c>shell_execute</c> tool, persisted at
/// <c>&lt;user-scope&gt;/config/shell-config.json</c>.
/// </summary>
public sealed record ShellConfig
{
    /// <summary>
    /// Creates a configuration with the defaults the tool relies on: the tool
    /// stays disabled, the sandbox and the root guardrail stay on, and the
    /// rule lists start empty. The constructor is the deserialization
    /// constructor because the source-generated JSON metadata assigns every
    /// property it knows about, which would otherwise replace these defaults
    /// with <c>default</c> whenever the file leaves a field out - silently
    /// switching the sandbox and the guardrail off.
    /// </summary>
    /// <param name="enabled">Master switch for the tool.</param>
    /// <param name="restrictToAllowedDirectories">Whether commands are confined to the working directory and the allow-list.</param>
    /// <param name="allowedDirectories">Directories outside the working directory that are allowed.</param>
    /// <param name="blockedCommands">Command fragments that are refused.</param>
    /// <param name="rootGuardrail">Whether destructive commands targeting the filesystem root are refused.</param>
    /// <param name="defaultTimeoutSeconds">Timeout applied when a call does not ask for one.</param>
    /// <param name="maxOutputBytes">Per-stream output cap applied when a call does not ask for one.</param>
    /// <param name="updatedAt">
    /// Last mutation time. <see cref="default"/> means "not stated", which is
    /// stamped with the current time.
    /// </param>
    [JsonConstructor]
    public ShellConfig(
        bool enabled = false,
        bool restrictToAllowedDirectories = true,
        List<AllowedDirectoryRule>? allowedDirectories = null,
        List<BlockedCommandRule>? blockedCommands = null,
        bool rootGuardrail = true,
        int defaultTimeoutSeconds = 120,
        int maxOutputBytes = 65536,
        DateTimeOffset updatedAt = default)
    {
        Enabled = enabled;
        RestrictToAllowedDirectories = restrictToAllowedDirectories;
        AllowedDirectories = allowedDirectories ?? [];
        BlockedCommands = blockedCommands ?? [];
        RootGuardrail = rootGuardrail;
        DefaultTimeoutSeconds = defaultTimeoutSeconds;
        MaxOutputBytes = maxOutputBytes;
        UpdatedAt = updatedAt == default ? DateTimeOffset.UtcNow : updatedAt;
    }

    /// <summary>Master switch. The tool stays disabled until the user opts in.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Whether commands are confined to the working directory and the
    /// allow-list. When false, any existing directory on the host is allowed.
    /// </summary>
    public bool RestrictToAllowedDirectories { get; init; }

    /// <summary>Directories outside the working directory that are allowed.</summary>
    public List<AllowedDirectoryRule> AllowedDirectories { get; init; }

    /// <summary>Command fragments that are refused.</summary>
    public List<BlockedCommandRule> BlockedCommands { get; init; }

    /// <summary>Whether destructive commands targeting the filesystem root are refused.</summary>
    public bool RootGuardrail { get; init; }

    /// <summary>Timeout applied when a call does not ask for one.</summary>
    public int DefaultTimeoutSeconds { get; init; }

    /// <summary>Per-stream output cap applied when a call does not ask for one.</summary>
    public int MaxOutputBytes { get; init; }

    /// <summary>Last mutation time, generated in backend code.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}
