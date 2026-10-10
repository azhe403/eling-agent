using System.Text.Json.Serialization;
using Eling.Backend.Updates;

namespace Eling.Backend.Dtos;

/// <summary>
/// Update notice carried inside <c>memory_recall</c> responses. Present only
/// when an update is available; omitted otherwise so quiet payloads stay lean.
/// </summary>
public sealed class UpdateNoticeDto
{
    [JsonPropertyName("currentVersion")]
    public string CurrentVersion { get; set; } = string.Empty;

    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; set; } = string.Empty;

    [JsonPropertyName("releaseUrl")]
    public string ReleaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("installHint")]
    public string InstallHint { get; set; } = string.Empty;

    /// <summary>
    /// Maps a status to a notice, or <c>null</c> when there is nothing to offer.
    /// </summary>
    public static UpdateNoticeDto? FromStatus(UpdateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (!status.UpdateAvailable || string.IsNullOrWhiteSpace(status.LatestVersion))
        {
            return null;
        }

        return new UpdateNoticeDto
        {
            CurrentVersion = status.CurrentVersion,
            LatestVersion = status.LatestVersion,
            ReleaseUrl = status.ReleaseUrl ?? string.Empty,
            InstallHint = InstallHintForCurrentPlatform(),
        };
    }

    /// <summary>One-line installer for the current OS, from <c>INSTALL.md</c>.</summary>
    public static string InstallHintForCurrentPlatform()
        => OperatingSystem.IsWindows()
            ? "irm https://raw.githubusercontent.com/azhe403/eling-agent/main/scripts/install.ps1 | iex"
            : "curl -fsSL https://raw.githubusercontent.com/azhe403/eling-agent/main/scripts/install.sh | bash";
}
