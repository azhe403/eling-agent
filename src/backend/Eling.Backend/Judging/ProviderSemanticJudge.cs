using System.ClientModel;
using System.Diagnostics;
using Eling.Core.Memory;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Eling.Backend.Judging;

/// <summary>
/// Calls a separate, user-configured OpenAI-compatible provider to judge memory
/// relationships. Intentionally does not reuse the Desktop agent's gateway: that one
/// takes the agent <c>ProviderStore</c> by concrete type, and coupling a memory-system
/// decision to Desktop configuration is exactly what the separate setting avoids.
/// </summary>
/// <remarks>
/// Fully orchestrated through a native Polly v8 pipeline: circuit breaker on the
/// outside, retry with exponential backoff in the middle, and dynamic per-attempt
/// escalating timeout (10s -> 15s) on the inside. Every failure mode throws, on purpose.
/// The caller (<c>MemoryService</c>) owns the fallback: it degrades to the heuristic
/// decision and records the reason.
/// </remarks>
public sealed class ProviderSemanticJudge : ISemanticJudge, ISemanticSummarizer
{
    private static readonly ResiliencePropertyKey<int> BaseTimeoutKey = new("BaseTimeout");
    private static readonly ResiliencePropertyKey<int> AttemptIndexKey = new("AttemptIndex");

    private readonly SemanticJudgeStore _store;
    private readonly ILogger<ProviderSemanticJudge> _logger;
    private readonly SmartSaveOptions _smartSave;

    // Retry allows 1 retry attempt after the initial try (2 attempts total).
    // Attempt 1 gets baseSeconds, attempt 2 gets 1.5x baseSeconds.
    // If the second attempt also times out or fails, Polly retries are exhausted and
    // the caller falls back to the heuristic decision.
    private const int MaxRetryAttempts = 1;

    // After five consecutive failures the breaker opens and calls fail fast for 30s, so
    // a genuinely-down provider no longer stalls every save until process exit.
    private const int CircuitOpenAfterConsecutiveFailures = 5;
    private static readonly TimeSpan CircuitBreakDuration = TimeSpan.FromSeconds(30);

    private readonly ResiliencePipeline<ClientResult<ChatCompletion>> _pipeline;

    public ProviderSemanticJudge(
        SemanticJudgeStore store,
        ILogger<ProviderSemanticJudge> logger,
        SmartSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
        _smartSave = options ?? new SmartSaveOptions();
        _pipeline = BuildPipeline();
    }

    public async Task<SemanticJudgement> JudgeAsync(
        Memory incoming,
        IReadOnlyCollection<Memory> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(candidates);

        if (!_store.TryGetConfig(out var baseUrl, out var model, out var apiKey, out var customTimeoutSeconds))
        {
            throw new InvalidOperationException("Semantic judge is not configured.");
        }

        if (candidates.Count == 0)
        {
            return new SemanticJudgement(SemanticRelation.Unrelated, 0.0, "no candidates");
        }

        // Single source of truth: if semantic-judge.json has a timeoutSeconds, that takes
        // precedence without touching C# code. Otherwise, the single InitialJudgeTimeout
        // in SmartSaveOptions controls attempt 1, and attempt 2 scales automatically (1.5x).
        var baseSeconds = customTimeoutSeconds is > 0
            ? customTimeoutSeconds.Value
            : (int)_smartSave.InitialJudgeTimeout.TotalSeconds;

        var client = CreateClient(baseUrl, model, apiKey);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(JudgePrompt.SystemInstructions),
            new UserChatMessage(JudgePrompt.Build(incoming, candidates)),
        };

        var started = Stopwatch.GetTimestamp();
        _logger.LogDebug(
            "Judge calling model={Model} for {CandidateCount} candidate(s) of type {IncomingType} (baseTimeout={BaseSeconds}s)",
            model,
            candidates.Count,
            incoming.Type,
            baseSeconds);

        var text = await CompleteChatAsync(client, messages, baseSeconds, "Judge", candidates.Count, cancellationToken);

        // Null means the answer was unusable; the caller treats that as "create".
        var parsed = JudgePrompt.Parse(text, candidates);
        if (parsed is null)
        {
            _logger.LogWarning(
                "Judge returned an unparseable answer after {ElapsedMs} ms ({Length} chars); treating as unrelated",
                ElapsedMs(started),
                text.Length);
            return new SemanticJudgement(SemanticRelation.Unrelated, 0.0, "judge returned an unusable answer");
        }

        var verdict = parsed.GetValueOrDefault();

        // Never log the reason verbatim: it is model-authored and can echo memory text,
        // and log files outlive the store they describe.
        _logger.LogInformation(
            "Judge verdict {Relation} confidence={Confidence:F2} target={TargetId} after {ElapsedMs} ms ({CandidateCount} candidate(s))",
            verdict.Relation,
            verdict.Confidence,
            verdict.TargetId is null ? "none" : verdict.TargetId.Value.Value,
            ElapsedMs(started),
            candidates.Count);

        return verdict;
    }

    public async Task<MemorySummary> SummarizeAsync(
        IReadOnlyCollection<Memory> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return new MemorySummary([], "judge");
        }

        if (!_store.TryGetConfig(out var baseUrl, out var model, out var apiKey, out var customTimeoutSeconds))
        {
            throw new InvalidOperationException("Semantic judge is not configured.");
        }

        // Same timeout policy as the judge: config value wins, otherwise the
        // escalating SmartSaveOptions timeouts apply inside the shared pipeline.
        var baseSeconds = customTimeoutSeconds is > 0
            ? customTimeoutSeconds.Value
            : (int)_smartSave.InitialJudgeTimeout.TotalSeconds;

        var client = CreateClient(baseUrl, model, apiKey);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(RecallSummaryPrompt.SystemInstructions),
            new UserChatMessage(RecallSummaryPrompt.Build(items)),
        };

        var started = Stopwatch.GetTimestamp();
        _logger.LogDebug(
            "Recall summary calling model={Model} for {ItemCount} item(s) (baseTimeout={BaseSeconds}s)",
            model,
            items.Count,
            baseSeconds);

        var text = await CompleteChatAsync(client, messages, baseSeconds, "Recall summary", items.Count, cancellationToken);

        var paragraphs = RecallSummaryPrompt.Parse(text);
        if (paragraphs.Count == 0)
        {
            // Unusable answer: not an error. The caller falls back to a
            // deterministic digest, exactly as a save falls back to the
            // heuristic when the judge cannot decide.
            _logger.LogWarning(
                "Recall summary returned an unusable answer after {ElapsedMs} ms ({Length} chars)",
                ElapsedMs(started),
                text.Length);
            return new MemorySummary([], "judge");
        }

        _logger.LogInformation(
            "Recall summary produced {ParagraphCount} paragraph(s) after {ElapsedMs} ms ({ItemCount} item(s))",
            paragraphs.Count,
            ElapsedMs(started),
            items.Count);

        return new MemorySummary(paragraphs, "judge");
    }

    private static ChatClient CreateClient(string baseUrl, string model, string apiKey)
        => new(
            model,
            new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "no-key" : apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(baseUrl.TrimEnd('/') + "/") });

    /// <summary>
    /// Runs one chat completion through the shared resilience pipeline and
    /// returns the concatenated text parts. Every failure mode throws, on
    /// purpose: the caller owns the fallback (heuristic decision for the
    /// judge, deterministic digest for the summary).
    /// </summary>
    private async Task<string> CompleteChatAsync(
        ChatClient client,
        List<ChatMessage> messages,
        int baseSeconds,
        string operation,
        int itemCount,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        try
        {
            context.Properties.Set(BaseTimeoutKey, baseSeconds);
            var completion = await _pipeline.ExecuteAsync(
                async ctx => await client.CompleteChatAsync(messages, cancellationToken: ctx.CancellationToken),
                context);

            // Project the text parts without naming the SDK's content-part enum: a null
            // Text is exactly a non-text part, and keeping to the stable surface is
            // cheaper than tracking which SDK version renamed it.
            return string.Concat(completion.Value.Content
                .Select(part => part.Text)
                .Where(value => !string.IsNullOrEmpty(value)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "{Operation} cancelled by caller after {ElapsedMs} ms (budget {BudgetMs} ms); caller will fall back",
                operation,
                ElapsedMs(started),
                (int)_smartSave.JudgeTimeout.TotalMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Operation} call failed after {ElapsedMs} ms for {ItemCount} item(s)",
                operation,
                ElapsedMs(started),
                itemCount);
            throw;
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    private ResiliencePipeline<ClientResult<ChatCompletion>> BuildPipeline()
        => new ResiliencePipelineBuilder<ClientResult<ChatCompletion>>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<ClientResult<ChatCompletion>>
            {
                FailureRatio = 1.0,
                SamplingDuration = TimeSpan.FromSeconds(60),
                MinimumThroughput = CircuitOpenAfterConsecutiveFailures,
                BreakDuration = CircuitBreakDuration,
                ShouldHandle = new PredicateBuilder<ClientResult<ChatCompletion>>()
                    .Handle<HttpRequestException>()
                    .Handle<ClientResultException>()
                    .Handle<TimeoutRejectedException>(),
            })
            .AddRetry(new RetryStrategyOptions<ClientResult<ChatCompletion>>
            {
                MaxRetryAttempts = MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(200),
                MaxDelay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder<ClientResult<ChatCompletion>>()
                    .Handle<HttpRequestException>()
                    .Handle<ClientResultException>()
                    .Handle<TimeoutRejectedException>(),
                OnRetry = args =>
                {
                    // Escalate the attempt index for the inner TimeoutGenerator
                    args.Context.Properties.Set(AttemptIndexKey, args.AttemptNumber + 1);
                    _logger.LogInformation(
                        "Judge attempt {Attempt} failed ({Reason}); retrying with escalated timeout",
                        args.AttemptNumber + 1,
                        args.Outcome.Exception?.GetType().Name ?? "unknown");
                    return default;
                },
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                TimeoutGenerator = args =>
                {
                    var baseSeconds = args.Context.Properties.TryGetValue(BaseTimeoutKey, out var b) ? b : 10;
                    var attempt = args.Context.Properties.TryGetValue(AttemptIndexKey, out var a) ? a : 0;
                    var seconds = attempt switch
                    {
                        0 => baseSeconds,
                        _ => (int)Math.Ceiling(baseSeconds * 1.5)
                    };
                    return ValueTask.FromResult(TimeSpan.FromSeconds(seconds));
                },
            })
            .Build();

    private static long ElapsedMs(long started)
        => (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}
