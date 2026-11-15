using System.Text.Json;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Shared helpers for the commit-lease tool family (<c>commit_lease</c>,
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

    /// <summary>Minimum allowed length for the lease tools' caller-supplied <c>caller_id</c>, after trimming. Ids are caller-supplied and format-agnostic; this floor only rejects trivially short ids like <c>"1"</c> that would collide too easily in a shared store.</summary>
    public const int MinCallerIdLength = 24;

    /// <summary>
    /// Sanity-check a caller-supplied identifier (the lease tools' <c>caller_id</c>). Ids are
    /// caller-supplied and format-agnostic: pass your own stable unique string - a slug, a
    /// UUID, or your host's session id - the same value on every call. Ids must be non-blank
    /// and long enough that trivial ids like <c>"1"</c> do not collide in a shared store.
    /// </summary>
    public static bool IsCallerIdPlausible(string? callerId)
        => !string.IsNullOrWhiteSpace(callerId) && callerId.Trim().Length >= MinCallerIdLength;

    public static string CallerIdTooShortMessage(string? callerId)
    {
        var length = string.IsNullOrWhiteSpace(callerId) ? 0 : callerId.Trim().Length;
        return $"caller_id must be a non-blank string of at least {MinCallerIdLength} characters identifying the caller (got {length}). Any stable string you choose is accepted; keep it the same across calls - the format is not validated.";
    }

    public static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, IgnoreNull);

    private static readonly JsonSerializerOptions IgnoreNull = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
