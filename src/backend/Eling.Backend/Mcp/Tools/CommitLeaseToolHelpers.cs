using System.Text.Json;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Shared helpers for the commit-lease tool family (<c>commit_check_conflict</c>,
/// <c>commit_wait</c>, <c>commit_list_active</c>, …). Not an MCP tool; registered in
/// nothing. Keep every helper here single-purpose and branch-safe.
/// </summary>
internal static class CommitLeaseToolHelpers
{
    public static bool BranchMatches(string? leaseBranch, string? branchKey)
    {
        if (branchKey is null)
        {
            return true;
        }

        return string.Equals(leaseBranch?.Trim(), branchKey, BranchComparison);
    }

    internal static StringComparison BranchComparison
        = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static long AgeSeconds(DateTimeOffset now, DateTimeOffset since)
        => (long)Math.Max(0, (now - since).TotalSeconds);

    public static string? NormalizeIntent(string? intent)
        => string.IsNullOrWhiteSpace(intent) ? null : intent.Trim();

    /// <summary>Minimum allowed length for <c>session_id</c>, after trimming. Sized to carry a full entropy token; short ids collide too easily in a shared store.</summary>
    public const int MinSessionIdLength = 32;

    /// <summary>
    /// Sanity-check a caller-supplied <c>session_id</c>. Ids must be non-blank and long enough
    /// to carry their own entropy (trivial ids like <c>"1"</c> make the store useless).
    /// </summary>
    public static bool IsSessionIdPlausible(string? sessionId)
        => !string.IsNullOrWhiteSpace(sessionId) && sessionId.Trim().Length >= MinSessionIdLength;

    public static string SessionIdTooShortMessage(string? sessionId)
    {
        var length = string.IsNullOrWhiteSpace(sessionId) ? 0 : sessionId.Trim().Length;
        return $"session_id must be non-blank and at least {MinSessionIdLength} characters (got {length}).";
    }

    public static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, IgnoreNull);

    private static readonly JsonSerializerOptions IgnoreNull = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
