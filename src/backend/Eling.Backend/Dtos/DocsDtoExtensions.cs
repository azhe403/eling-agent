using Eling.Core.Docs;

namespace Eling.Backend.Dtos;

/// <summary>
/// Extension methods for converting domain enums to DTOs.
/// </summary>
public static class DocsDtoExtensions
{
    public static DocOwnershipDto ToDto(this Eling.Core.Docs.DocOwnership o) => o switch
    {
        Eling.Core.Docs.DocOwnership.Managed => DocOwnershipDto.Managed,
        Eling.Core.Docs.DocOwnership.External => DocOwnershipDto.External,
        _ => throw new ArgumentOutOfRangeException(nameof(o))
    };

    public static DocOriginDto ToDto(this Eling.Core.Docs.DocOrigin o) => o switch
    {
        Eling.Core.Docs.DocOrigin.Current => DocOriginDto.Current,
        Eling.Core.Docs.DocOrigin.Ancestor => DocOriginDto.Ancestor,
        Eling.Core.Docs.DocOrigin.Shard => DocOriginDto.Shard,
        Eling.Core.Docs.DocOrigin.Global => DocOriginDto.Global,
        _ => throw new ArgumentOutOfRangeException(nameof(o))
    };

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

    public static OfferStateDto ToDto(this Eling.Core.Docs.OfferState s) => s switch
    {
        Eling.Core.Docs.OfferState.Unasked => OfferStateDto.Unasked,
        Eling.Core.Docs.OfferState.Accepted => OfferStateDto.Accepted,
        Eling.Core.Docs.OfferState.Declined => OfferStateDto.Declined,
        Eling.Core.Docs.OfferState.Never => OfferStateDto.Never,
        _ => throw new ArgumentOutOfRangeException(nameof(s))
    };
}