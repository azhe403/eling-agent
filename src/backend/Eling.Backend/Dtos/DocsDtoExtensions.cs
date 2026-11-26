using Eling.Core.Docs;

namespace Eling.Backend.Dtos;

/// <summary>
/// Extension methods for converting domain enums to DTOs.
/// </summary>
public static class DocsDtoExtensions
{
    public static DocKindDto ToDto(this Eling.Core.Docs.DocKind k) => k switch
    {
        Eling.Core.Docs.DocKind.Spec => DocKindDto.Spec,
        Eling.Core.Docs.DocKind.Plan => DocKindDto.Plan,
        Eling.Core.Docs.DocKind.Doc => DocKindDto.Doc,
        Eling.Core.Docs.DocKind.Scratch => DocKindDto.Scratch,
        _ => throw new ArgumentOutOfRangeException(nameof(k))
    };

    public static ReferenceKindDto ToDto(this Eling.Core.Docs.ReferenceKind k) => k switch
    {
        Eling.Core.Docs.ReferenceKind.Symbol => ReferenceKindDto.Symbol,
        Eling.Core.Docs.ReferenceKind.Path => ReferenceKindDto.Path,
        Eling.Core.Docs.ReferenceKind.AnchorLink => ReferenceKindDto.AnchorLink,
        _ => throw new ArgumentOutOfRangeException(nameof(k))
    };

    public static RelationKindDto ToDto(this Eling.Core.Docs.RelationKind k) => k switch
    {
        Eling.Core.Docs.RelationKind.Implements => RelationKindDto.Implements,
        Eling.Core.Docs.RelationKind.Supersedes => RelationKindDto.Supersedes,
        Eling.Core.Docs.RelationKind.References => RelationKindDto.References,
        _ => throw new ArgumentOutOfRangeException(nameof(k))
    };

    public static StalenessKindDto ToDto(this Eling.Core.Docs.StalenessKind k) => k switch
    {
        Eling.Core.Docs.StalenessKind.UnresolvedReference => StalenessKindDto.UnresolvedReference,
        Eling.Core.Docs.StalenessKind.SupersededWithoutReplacement => StalenessKindDto.SupersededWithoutReplacement,
        Eling.Core.Docs.StalenessKind.StaleDraft => StalenessKindDto.StaleDraft,
        Eling.Core.Docs.StalenessKind.Untouched => StalenessKindDto.Untouched,
        _ => throw new ArgumentOutOfRangeException(nameof(k))
    };

}