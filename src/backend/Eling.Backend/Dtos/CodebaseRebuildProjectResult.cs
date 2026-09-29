namespace Eling.Backend.Dtos;

/// <summary>
/// Outcome of one project's index pass. A failing project is an expected
/// outcome, not a job failure: a locked or corrupt sibling DB is recorded
/// here and the job still completes. Counts are zero when
/// <paramref name="Ok"/> is false.
/// </summary>
public sealed record CodebaseRebuildProjectResult(
    string ProjectRoot,
    bool Ok,
    int Files,
    int Chunks,
    int Skipped,
    int Deleted,
    string? Error);
