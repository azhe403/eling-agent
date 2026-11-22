namespace Eling.Backend.Dtos;

/// <summary>
/// Request to scan for documents.
/// </summary>
public sealed record DocScanRequest(
    bool Full = false,
    List<string>? Paths = null);