namespace Eling.Core.Docs;

/// <summary>
/// Offer check result.
/// </summary>
public sealed record OfferCheckResult(
    bool ShouldOffer,
    OfferState State,
    string Reason);