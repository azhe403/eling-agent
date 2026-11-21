namespace Eling.Backend.Dtos;

/// <summary>
/// Document reference DTO.
/// </summary>
public sealed record DocRefDto(
    string DocId,
    string Anchor,
    string RawText,
    ReferenceKindDto RefKind,
    bool Resolved,
    string? ResolvedPath,
    int? ResolvedLine,
    DateTimeOffset? ResolvedAt)
{
    public static DocRefDto From(Eling.Core.Docs.DocRef @ref) => new(
        @ref.DocId.Value,
        @ref.Anchor,
        @ref.RawText,
        @ref.RefKind.ToDto(),
        @ref.Resolved,
        @ref.ResolvedPath,
        @ref.ResolvedLine,
        @ref.ResolvedAt);
}