namespace Eling.Backend.Dtos;

/// <summary>
/// Document relation DTO.
/// </summary>
public sealed record DocRelationDto(
    string SourceId,
    string TargetId,
    RelationKindDto Kind)
{
    public static DocRelationDto From(Eling.Core.Docs.DocRelation relation) => new(
        relation.SourceId.Value,
        relation.TargetId.Value,
        relation.Kind.ToDto());
}