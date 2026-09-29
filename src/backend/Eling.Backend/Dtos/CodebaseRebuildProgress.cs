namespace Eling.Backend.Dtos;

/// <summary>
/// Immutable snapshot of one rebuild job, returned by the start request and
/// the status endpoint, and streamed as JSON over the rebuild SSE channel.
/// <paramref name="IsRunning"/> is the only lifecycle flag the dashboard needs;
/// per-project outcomes live in <paramref name="Results"/>.
/// </summary>
public sealed record CodebaseRebuildProgress(
    string JobId,
    string Scope,
    bool Full,
    bool IsRunning,
    int Done,
    int Total,
    IReadOnlyList<string> SkippedRoots,
    string? CurrentProject,
    IReadOnlyList<CodebaseRebuildProjectResult> Results,
    string? Error);
