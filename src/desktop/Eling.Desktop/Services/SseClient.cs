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
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                OnStatusChanged?.Invoke("connecting");
                _logger.LogDebug("Connecting to SSE stream...");

                var request = new HttpRequestMessage(HttpMethod.Get, "api/events/memories");
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
                    if (string.IsNullOrWhiteSpace(line)) continue;

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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SSE connection lost, reconnecting in 3s...");
                OnStatusChanged?.Invoke("error");

                try
                {
                    await Task.Delay(3000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
    }
}
