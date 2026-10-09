namespace Eling.Core.Tools;

/// <summary>
/// Per-user tool enablement policy, persisted at
/// <c>&lt;user-scope&gt;/config/tools-policy.json</c>.
/// </summary>
public sealed record ToolPolicyConfig
{
    /// <summary>Tool names explicitly disabled by the user. Empty means all tools enabled.</summary>
    public List<string> DisabledTools { get; init; } = new();

    /// <summary>Last mutation time, generated in backend code.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}
