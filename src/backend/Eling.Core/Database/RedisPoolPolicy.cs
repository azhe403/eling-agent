namespace Eling.Core.Database;

/// <summary>
/// The two decisions the connection pool makes about the entries it holds:
/// which one has been idle long enough to drop, and which one leaves when the
/// cap is reached. Both are pure, so the rules can be tested without a Redis
/// server and without waiting for a timer to fire.
/// </summary>
public static class RedisPoolPolicy
{
    /// <summary>
    /// True when an entry has not been used for at least
    /// <paramref name="idleTimeout"/>. An entry used exactly at the cutoff
    /// counts as idle, so the comparison is "at least", not "more than".
    /// </summary>
    public static bool IsIdle(DateTimeOffset lastUsed, DateTimeOffset now, TimeSpan idleTimeout) =>
        now - lastUsed >= idleTimeout;

    /// <summary>
    /// The entry that was used longest ago, or <c>null</c> when there is
    /// nothing to evict. Ties keep the first one seen, which is enough for a
    /// cap: any of two equally old entries may go.
    /// </summary>
    public static string? PickLeastRecentlyUsed(IReadOnlyDictionary<string, DateTimeOffset> lastUsed)
    {
        string? oldestKey = null;
        var oldest = DateTimeOffset.MaxValue;
        foreach (var pair in lastUsed)
        {
            if (pair.Value < oldest)
            {
                oldest = pair.Value;
                oldestKey = pair.Key;
            }
        }
        return oldestKey;
    }
}