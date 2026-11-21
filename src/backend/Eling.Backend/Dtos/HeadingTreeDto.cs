namespace Eling.Backend.Dtos;

/// <summary>
/// Heading tree node for map mode.
/// </summary>
public sealed record HeadingTreeDto(
    string Anchor,
    string Heading,
    int Level);