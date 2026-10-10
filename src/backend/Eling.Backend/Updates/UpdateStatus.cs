namespace Eling.Backend.Updates;

/// <summary>
/// Point-in-time result of an update check: the running version, the newest
/// known release on the configured channel, and whether it counts as newer.
/// </summary>
public sealed record UpdateStatus
{
    /// <summary>Version of the running binary, from assembly metadata.</summary>
    public string CurrentVersion { get; init; } = string.Empty;

    /// <summary>Newest known release tag on the channel, if the check ever succeeded.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>Whether <see cref="LatestVersion"/> counts as newer than <see cref="CurrentVersion"/>.</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>Link to the release page, when a newer release is known.</summary>
    public string? ReleaseUrl { get; init; }

    /// <summary>Short excerpt of the release notes, when provided.</summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>When this status was produced, generated in backend code.</summary>
    public DateTimeOffset CheckedAt { get; init; }

    /// <summary>Channel the candidate was selected from (for example <c>prerelease</c>).</summary>
    public string Channel { get; init; } = string.Empty;
}
