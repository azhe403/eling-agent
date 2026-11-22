namespace Eling.Backend.Dtos;

/// <summary>
/// Request to answer the managed docs offer.
/// </summary>
public sealed record DocOfferRequest(
    string Answer); // accepted, declined, never