using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>One lease that left the active set, newest first. <c>Checkout</c> is the wire name <c>checkout</c> — the working-copy root the lease lived in, present so a snapshot that spans checkouts can render a per-row workspace.</summary>
public sealed record CommitLeaseReleasedDto
{
    [JsonPropertyName("lease_id")]
    public required string LeaseId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("since")]
    public required DateTimeOffset Since { get; init; }

    [JsonPropertyName("released_at")]
    public required DateTimeOffset? ReleasedAt { get; init; }

    [JsonPropertyName("release_reason")]
    public required string? ReleaseReason { get; init; }

    [JsonPropertyName("intent")]
    public required string? Intent { get; init; }

    [JsonPropertyName("paths")]
    public required string[]? Paths { get; init; }

    [JsonPropertyName("checkout")]
    public required string Checkout { get; init; }
}
