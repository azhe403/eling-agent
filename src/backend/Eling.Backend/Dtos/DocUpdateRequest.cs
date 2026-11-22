namespace Eling.Backend.Dtos;

/// <summary>
/// Request to update an existing document.
/// </summary>
public sealed record DocUpdateRequest(
    string Content);