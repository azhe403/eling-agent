using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Eling.Desktop.Services;

public sealed class SseClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SseClient> _logger;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public event Action? OnMemoryChanged;
    public event Action? OnRuntimesChanged;
    public event Action<string>? OnStatusChanged;

    public SseClient(string baseUrl, ILogger<SseClient> logger)
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        _logger = logger;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listenTask = ListenAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var pipeline = BackendResilience.ForExceptions(_logger, name: "sse");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Every execution starts the ramp again at the base delay, so a stream
                // that was healthy reconnects quickly while a backend that never comes
                // up settles onto the cap instead of being retried in a tight loop.
                //
                // ExecuteAsync<bool> around ValueTask<bool>, not ExecuteAsync<object?>
                // around a plain Task. ValueTask<object?> has no ctor taking a
                // non-generic Task, so the compiler picks ValueTask(object?), which
                // stores the Task as the *result* and completes instantly. The stream
                // then runs unobserved, this policy never sees a failure so it never
                // retries, and the loop below opens a new connection every delay.
                //
                // The result type has to be bool and not object?: Task<T> is invariant,
                // so Task<bool> would not bind to ValueTask<object?> either - the
                // types have to match on both sides or the same trap reopens.
                await pipeline.ExecuteAsync<bool>(
                    token => new ValueTask<bool>(RunStreamAsync(token)),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SSE exhausted its retry budget, starting a fresh cycle");
                OnStatusChanged?.Invoke("error");
            }

            // Polly only backs off on failure, and a stream the server closes
            // immediately counts as a success, so reconnecting straight away would
            // spin. Pause before every new cycle, using the same policy delay.
            try
            {
                await Task.Delay(BackendResilience.BaseDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Holds the stream open until the server closes it or the caller cancels.
    /// </summary>
    /// <remarks>
    /// The result is never read - <c>ForExceptions</c> retries on exception, not on
    /// value. It returns <c>Task&lt;bool&gt;</c> rather than a plain <c>Task</c> so
    /// the call site can bind <c>ValueTask&lt;bool&gt;(Task&lt;bool&gt;)</c>. A
    /// non-generic Task silently resolves to the <c>ValueTask(object?)</c>
    /// constructor instead, which starts the stream without awaiting it and turns
    /// the retry policy off.
    /// </remarks>
    private async Task<bool> RunStreamAsync(CancellationToken cancellationToken)
    {
        OnStatusChanged?.Invoke("connecting");
        _logger.LogDebug("Connecting to SSE stream...");

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/events/memories");
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        OnStatusChanged?.Invoke("connected");
        _logger.LogInformation("SSE stream connected");

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue; // SSE keep-alive / event separator

            if (line.StartsWith("data:"))
            {
                var data = line[5..].Trim();
                _logger.LogDebug("SSE event: {Data}", data);

                if (data == "runtimes")
                    OnRuntimesChanged?.Invoke();
                else if (!string.IsNullOrEmpty(data) && data != "connected")
                    OnMemoryChanged?.Invoke();
            }
        }

        // The server closed the stream. Not a failure: anything worth retrying was
        // already retried inside the policy, so returning hands control back to the
        // reconnect delay in the caller.
        return true;
    }

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
    }
}
