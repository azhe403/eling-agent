namespace Eling.Backend.Judging;

/// <summary>
/// Per-user configuration for the memory semantic judge, persisted at
/// <c>&lt;user-scope&gt;/config/semantic-judge.json</c>.
/// </summary>
/// <remarks>
/// Deliberately separate from the Eling Desktop agent provider
/// (<c>agent-provider.json</c>): the MCP host does not register the agent stack at
/// all, and borrowing that file would both conflate two domains and make a memory
/// system's behaviour depend on Desktop configuration. Per-user rather than
/// per-project, so the API key never lands in a committed <c>.eling</c> directory.
/// </remarks>
public sealed record SemanticJudgeConfig
{
    /// <summary>Whether the judge participates in memory saves. Defaults to off.</summary>
    public bool Enabled { get; init; }

    public string? BaseUrl { get; init; }

    public string? Model { get; init; }

    public string? ApiKey { get; init; }

    /// <summary>
    /// Optional initial attempt timeout in seconds (default 10). If configured,
    /// attempt 1 uses this duration, attempt 2 scales to 1.5x, and no C# code edits
    /// are needed.
    /// </summary>
    public int? TimeoutSeconds { get; init; }
}
