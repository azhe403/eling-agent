using System.Text.RegularExpressions;

namespace Eling.Core;

internal static class IntentionTriggerMatcher
{
    public static (bool Matched, bool Expired) Match(Intention intention, MemoryRecallContext? context, DateTimeOffset now)
    {
        if (IsExpired(intention, now)) return (false, true);
        var matched = intention.TriggerType switch
        {
            TriggerType.Topic => MatchesTopic(intention, context?.Topics),
            TriggerType.FilePattern => MatchesFilePattern(intention, context?.FilePath),
            TriggerType.TimeBased => MatchesTimeBased(intention, now),
            _ => false
        };
        return (matched, false);
    }
    public static bool IsExpired(Intention intention, DateTimeOffset now) => intention.ExpiresAt is not null && intention.ExpiresAt.Value <= now;
    public static bool IsOutstanding(Intention intention, DateTimeOffset now) => intention.Status == MemoryStatus.Active && (intention.ExpiresAt is null || intention.ExpiresAt.Value > now);
    private static bool MatchesTopic(Intention intention, IReadOnlyCollection<string>? topics)
    {
        if (topics is null || topics.Count == 0) return false;
        var pattern = intention.Pattern?.Trim();
        if (!string.IsNullOrEmpty(pattern)) return topics.Any(t => ContainsCi(t, pattern));
        return topics.Any(topic => intention.Tags.Any(tag => ContainsCi(topic, tag)) || ContainsCi(intention.Description, topic));
    }
    private static bool MatchesFilePattern(Intention intention, string? filePath) => !string.IsNullOrWhiteSpace(intention.Pattern) && !string.IsNullOrWhiteSpace(filePath) && GlobPattern.IsMatch(intention.Pattern, filePath);
    private static bool MatchesTimeBased(Intention intention, DateTimeOffset now) => intention.ExpiresAt is { } expires && expires > now && expires <= now + TimeSpan.FromHours(24);
    private static bool ContainsCi(string haystack, string needle) => haystack.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);
}
internal static class GlobPattern
{
    public static bool IsMatch(string glob, string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(glob);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var regex = GlobToRegex(glob);
        var normalized = filePath.Replace('\\', '/');
        return regex.IsMatch(normalized);
    }
    private static Regex GlobToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob.Trim().Replace('\\', '/')).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", ".") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
