using Eling.Core.Docs;

namespace Eling.Backend.Dtos;

/// <summary>
/// Response for doc_offer_managed.
/// </summary>
public sealed record OfferResponse(
    bool ShouldOffer,
    OfferStateDto State,
    string Reason,
    string? Action = null) // "check" or "answer"
{
    public static OfferResponse FromCheck(OfferCheckResult result) =>
        new(result.ShouldOffer, result.State.ToDto(), result.Reason, "check");

    public static OfferResponse FromAnswer(OfferState state) =>
        new(false, state.ToDto(), "Answer recorded", "answer");
}