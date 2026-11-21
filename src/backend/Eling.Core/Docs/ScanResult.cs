namespace Eling.Core.Docs;

/// <summary>
/// Result of a scan operation.
/// </summary>
public sealed record ScanResult(
    int Indexed,
    int Skipped,
    int Deleted,
    int Errors,
    int TotalScanned,
    bool AlreadyRunning = false,
    IReadOnlyList<string>? ErrorMessages = null)
{
    public ScanResult(int indexed, int skipped, int deleted, int errors, int totalScanned, IReadOnlyList<string>? errorMessages = null)
        : this(indexed, skipped, deleted, errors, totalScanned, false, errorMessages)
    {
    }
}