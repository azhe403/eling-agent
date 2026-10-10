using System.Text.Json.Serialization;
using Eling.Backend.Updates;

namespace Eling.Backend.Dtos;

/// <summary>
/// Full update status for <c>GET api/update/status</c>, served from cache.
/// </summary>
public sealed class UpdateStatusDto
{
    [JsonPropertyName("currentVersion")]
    public string CurrentVersion { get; set; } = string.Empty;

    [JsonPropertyName("latestVersion")]
    public string? LatestVersion { get; set; }

    [JsonPropertyName("updateAvailable")]
    public bool UpdateAvailable { get; set; }

    [JsonPropertyName("releaseUrl")]
    public string? ReleaseUrl { get; set; }

    [JsonPropertyName("releaseNotes")]
    public string? ReleaseNotes { get; set; }

    [JsonPropertyName("checkedAt")]
    public DateTimeOffset CheckedAt { get; set; }

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    /// <summary>Maps a checker status to its wire shape.</summary>
    public static UpdateStatusDto FromStatus(UpdateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new UpdateStatusDto
        {
            CurrentVersion = status.CurrentVersion,
            LatestVersion = status.LatestVersion,
            UpdateAvailable = status.UpdateAvailable,
            ReleaseUrl = status.ReleaseUrl,
            ReleaseNotes = status.ReleaseNotes,
            CheckedAt = status.CheckedAt,
            Channel = status.Channel,
        };
    }
}
