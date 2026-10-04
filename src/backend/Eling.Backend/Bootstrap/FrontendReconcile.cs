namespace Eling.Backend.Bootstrap;

/// <summary>
/// Reconcile policy for the frontend dev server: what to do with port 4427
/// given a probe result, and how long to wait before trying again after
/// repeated failures.
/// </summary>
/// <remarks>
/// Pure and side-effect free so it can be tested without spawning processes or
/// binding ports. State that changes between rounds - whether the port is
/// listening, whether the frontend has responded recently - belongs to the
/// loop, not to this class.
/// </remarks>
internal static class FrontendReconcile
{
    private const int BaseDelaySeconds = 2;
    private const int MaxDelaySeconds = 60;

    /// <summary>
    /// Backoff exponent cap. 2 * 2^6 = 128 already exceeds
    /// <see cref="MaxDelaySeconds"/>, so anything larger would clamp anyway.
    /// Capping the exponent also keeps the shift inside int range.
    /// </summary>
    private const int MaxExponent = 6;

    private const double MinJitterFactor = 0.5;
    private const double MaxJitterFactor = 1.5;

    /// <summary>
    /// A frontend that holds the port but does not answer is hung rather than
    /// not yet started, so it has to be killed before being respawned.
    /// </summary>
    public static FrontendAction Decide(bool portListening, bool responding) =>
        (portListening, responding) switch
        {
            (true, true) => FrontendAction.None,
            (true, false) => FrontendAction.Restart,
            _ => FrontendAction.Start,
        };

    /// <summary>
    /// Wait before the next attempt. Doubles per consecutive failure and stops
    /// at <see cref="MaxDelaySeconds"/>, so a frontend that is genuinely broken
    /// (a compile error, say) cannot turn into a respawn storm that saturates
    /// the CPU.
    /// </summary>
    public static TimeSpan RestartDelayAfter(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return TimeSpan.Zero;

        var exponent = Math.Min(consecutiveFailures - 1, MaxExponent);
        var seconds = Math.Min(BaseDelaySeconds * (1 << exponent), MaxDelaySeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Reads the ELING_WATCH_DASHBOARD environment variable as a boolean.
    /// </summary>
    /// <remarks>
    /// This variable has been set in opencode.json all along but no code ever
    /// read it. Without an explicit gate, every backend process running from
    /// inside the repo takes over port 4427 - including test processes, which
    /// then kill each other's frontend.
    /// </remarks>
    public static bool IsWatchEnabled(string? raw) =>
        raw is not null
        && (raw.Equals("1", StringComparison.Ordinal)
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Spreads a backoff delay over 0.5x to 1.5x so two loops that happen to be
    /// active at once do not wake on the same second.
    /// </summary>
    public static TimeSpan WithJitter(TimeSpan baseDelay)
    {
        var factor = MinJitterFactor + ((MaxJitterFactor - MinJitterFactor) * Random.Shared.NextDouble());
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
    }
}