using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Eling.Desktop.Services;

/// <summary>
/// One resilience policy for every place the desktop waits on a backend. Every
/// profile shares the same shape: retry with exponential backoff starting at one
/// second and capped at thirty, then start over at one second the next time the
/// pipeline is used. A backend that is merely slow to boot therefore gets short
/// waits early, while one that is genuinely down stops being hammered.
/// </summary>
public static class BackendResilience
{
    /// <summary>Attempts before a pipeline gives up. The caller decides what to do next.</summary>
    public const int DefaultAttempts = 10;

    /// <summary>First pause between attempts.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling the exponential ramp settles on.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Retries while the operation throws, for work that either connects or does not.
    /// </summary>
    public static ResiliencePipeline ForExceptions(
        ILogger? logger = null,
        int maxAttempts = DefaultAttempts,
        TimeSpan? delay = null,
        TimeSpan? maxDelay = null,
        string name = "boot") =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(static ex => ex.InnerException is TimeoutException),
                MaxRetryAttempts = maxAttempts,
                Delay = delay ?? BaseDelay,
                MaxDelay = maxDelay ?? MaxDelay,
                BackoffType = DelayBackoffType.Exponential,
                OnRetry = Report<object>(logger, name),
            })
            .Build();

    /// <summary>
    /// Retries while the result is unusable. The caller decides what unusable means,
    /// so a backend that reported failure can be retried without also retrying one
    /// that reported success. That distinction matters where an API returns a plain
    /// value for both cases: an unreachable backend and a genuinely empty result come
    /// back the same way, and only the caller knows which one it is looking at.
    /// </summary>
    public static ResiliencePipeline<TResult> ForResult<TResult>(
        Func<TResult, bool> isFailure,
        ILogger? logger = null,
        int maxAttempts = DefaultAttempts,
        TimeSpan? delay = null,
        TimeSpan? maxDelay = null,
        string name = "boot")
    {
        ArgumentNullException.ThrowIfNull(isFailure);

        return new ResiliencePipelineBuilder<TResult>()
            .AddRetry(new RetryStrategyOptions<TResult>
            {
                ShouldHandle = new PredicateBuilder<TResult>().HandleResult(isFailure),
                MaxRetryAttempts = maxAttempts,
                Delay = delay ?? BaseDelay,
                MaxDelay = maxDelay ?? MaxDelay,
                BackoffType = DelayBackoffType.Exponential,
                OnRetry = Report<TResult>(logger, name),
            })
            .Build();
    }

    private static Func<OnRetryArguments<TResult>, ValueTask> Report<TResult>(ILogger? logger, string name) =>
        args =>
        {
            logger?.LogWarning(
                "{Pipeline}: attempt {Attempt} unusable ({Reason}), retrying after {Delay}",
                name,
                args.AttemptNumber + 1,
                args.Outcome.Exception?.GetType().Name ?? args.Outcome.Result?.ToString() ?? "null",
                args.RetryDelay);
            return default;
        };
}
