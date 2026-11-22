namespace Eling.Backend.Dtos;

/// <summary>
/// Request to create a new document.
/// </summary>
public sealed record DocSaveRequest(
    string RelPath,
    string Content,
    string? Kind = null,
    string? Topic = null,
    string? Status = null,
    int? Order = null);