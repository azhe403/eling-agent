using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Eling.Desktop.Services;

/// <summary>
/// One resilience story for every place the desktop waits on a backend that may
/// still be booting. The profiles differ only in budget and pause, so a stall
/// reads the same in a log wherever it happened.
/// </summary>
public static class BackendResilience
{
    /// <summary>Attempts and pause sized to cover a cold backend start, about 20 seconds.</summary>
    public const int BootAttempts = 40;

    public static readonly TimeSpan BootDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Retries while the operation throws, for work that either connects or does not.
    /// </summary>
    public static ResiliencePipeline ForExceptions(
        ILogger? logger = null,
        int maxAttempts = BootAttempts,
        TimeSpan? delay = null,
        string name = "boot") =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(static ex => ex.InnerException is TimeoutException),
                MaxRetryAttempts = maxAttempts,
                Delay = delay ?? BootDelay,
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
        int maxAttempts = BootAttempts,
        TimeSpan? delay = null,
        string name = "boot")
    {
        ArgumentNullException.ThrowIfNull(isFailure);

        return new ResiliencePipelineBuilder<TResult>()
            .AddRetry(new RetryStrategyOptions<TResult>
            {
                ShouldHandle = new PredicateBuilder<TResult>().HandleResult(isFailure),
                MaxRetryAttempts = maxAttempts,
                Delay = delay ?? BootDelay,
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
