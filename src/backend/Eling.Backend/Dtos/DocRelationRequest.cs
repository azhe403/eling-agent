namespace Eling.Backend.Dtos;

/// <summary>
/// Request to add a relation between documents.
/// </summary>
public sealed record DocRelationRequest(
    string TargetId,
    string Kind); // Implements, Supersedes, References