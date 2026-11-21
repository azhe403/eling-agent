using Eling.Core.Docs;

namespace Eling.Backend.Dtos;

/// <summary>
/// Response for doc_index.
/// </summary>
public sealed record DocIndexResponse(
    int Indexed,
    int Skipped,
    int Deleted,
    int Errors,
    int TotalScanned,
    bool AlreadyRunning,
    IReadOnlyList<string>? ErrorMessages)
{
    public static DocIndexResponse From(ScanResult result) =>
        new(result.Indexed, result.Skipped, result.Deleted, result.Errors, result.TotalScanned, result.AlreadyRunning, result.ErrorMessages);
}