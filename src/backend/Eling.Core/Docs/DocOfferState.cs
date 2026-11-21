namespace Eling.Core.Docs;

/// <summary>
/// Offer state per project.
/// </summary>
public sealed record DocOfferState(
    string ProjectHash,
    OfferState State,
    DateTimeOffset? AskedAt,
    DateTimeOffset? AnsweredAt,
    int SpecsSinceAsk);