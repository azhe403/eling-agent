namespace Eling.Backend.Dtos;

/// <summary>
/// Offer check result DTO.
/// </summary>
public sealed record OfferCheckResultDto(
    bool ShouldOffer,
    OfferStateDto State,
    string Reason);