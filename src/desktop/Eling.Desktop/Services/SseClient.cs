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
                await pipeline.ExecuteAsync<object?>(
                    token => new ValueTask<object?>(RunStreamAsync(token)),
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

    private async Task RunStreamAsync(CancellationToken cancellationToken)
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
    }

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
    }
}
