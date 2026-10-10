namespace Eling.Backend.Updates;

/// <summary>
/// A release selected as the update candidate: newest non-draft on the channel.
/// </summary>
public sealed record ReleaseCandidate
{
    /// <summary>Release tag as published (for example <c>v0.1.0-pre.32</c>).</summary>
    public string Tag { get; init; } = string.Empty;

    /// <summary>Link to the release page, when provided.</summary>
    public string? Url { get; init; }

    /// <summary>Excerpt of the release notes, when provided.</summary>
    public string? Notes { get; init; }
}
