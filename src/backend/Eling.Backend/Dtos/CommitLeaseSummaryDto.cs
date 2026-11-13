using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>One live commit or file lease. <c>LastHeartbeat</c> is the wire name <c>last_heartbeat</c>; <c>Checkout</c> is the wire name <c>checkout</c> — the working-copy root the lease was taken in, present so a snapshot that spans checkouts can render a per-row workspace.</summary>
public sealed record CommitLeaseSummaryDto
{
    [JsonPropertyName("lease_id")]
    public required string LeaseId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("branch")]
    public required string? Branch { get; init; }

    [JsonPropertyName("since")]
    public required DateTimeOffset Since { get; init; }

    [JsonPropertyName("last_heartbeat")]
    public required DateTimeOffset LastHeartbeat { get; init; }

    [JsonPropertyName("lease_age_seconds")]
    public required double LeaseAgeSeconds { get; init; }

    [JsonPropertyName("ttl_seconds")]
    public required int TtlSeconds { get; init; }

    [JsonPropertyName("paths")]
    public required string[]? Paths { get; init; }

    [JsonPropertyName("intent")]
    public required string? Intent { get; init; }

    [JsonPropertyName("checkout")]
    public required string Checkout { get; init; }
}
