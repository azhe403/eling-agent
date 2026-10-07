namespace Eling.Core.Memory;

public sealed class SmartSaveOptions
{
    public double DuplicateThreshold { get; set; } = 0.60;

    public double CrossScopeDuplicateThreshold { get; set; } = 0.75;

    public double NearMatchThreshold { get; set; } = 0.35;

    public bool EnableFuzzyMatch { get; set; } = true;

    private TimeSpan? _judgeTimeout;

    /// <summary>
    /// Initial attempt timeout for the semantic judge (default 10s).
    /// Changing this value in one place automatically scales subsequent attempt
    /// timeouts (1.5x) and the outer fail-safe deadline <see cref="JudgeTimeout"/> (2.5x + 5s).
    /// </summary>
    public TimeSpan InitialJudgeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Overall safety budget for <see cref="ISemanticJudge.JudgeAsync"/>.
    /// Computed automatically from <see cref="InitialJudgeTimeout"/> so adjusting
    /// the initial timeout in one place automatically keeps the outer fail-safe
    /// deadline in sync.
    /// </summary>
    public TimeSpan JudgeTimeout
    {
        get => _judgeTimeout ?? TimeSpan.FromSeconds(Math.Ceiling(InitialJudgeTimeout.TotalSeconds * 2.5 + 5));
        set => _judgeTimeout = value;
    }
}
