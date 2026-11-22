using Eling.Core.Docs;

namespace Eling.Backend.Dtos;

/// <summary>
/// Offer check result DTO.
/// </summary>
public sealed record OfferCheckResultDto(
    bool ShouldOffer,
    OfferStateDto State,
    string Reason)
{
    public static OfferCheckResultDto From(OfferCheckResult result) => new(
        result.ShouldOffer,
        result.State.ToDto(),
        result.Reason
    );
}