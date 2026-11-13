namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Wire shape of the holder info reported by <c>commit_check_conflict</c> and <c>commit_wait</c>.
/// One definition so both tools return identical holder payloads.
/// </summary>
internal sealed record HolderInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("holder_session_id")]
    public required string HolderSessionId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("since")]
    public required DateTimeOffset Since { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("lease_age_seconds")]
    public required long LeaseAgeSeconds { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("intent")]
    public required string? Intent { get; init; }
}
