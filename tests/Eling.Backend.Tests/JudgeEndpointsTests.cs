using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Eling.Backend.Tests;

[Collection(ElingDataDirCollection.Name)]
public class JudgeEndpointsTests : IAsyncLifetime, IDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    [Fact]
    public async Task GetConfig_ReturnsCurrentView()
    {
        var client = _client!;
        var res = await client.GetAsync("/api/judge/config");
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("enabled", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hasApiKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("isConfigured", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PutConfig_UpdatesSettings_WithoutLeakingKey()
    {
        var client = _client!;
        var put = await client.PutAsJsonAsync("/api/judge/config", new
        {
            enabled = true,
            baseUrl = "https://ai.example.test/v1",
            model = "judge-model-1",
            apiKey = "secret-judge-key",
            timeoutSeconds = 12
        });
        put.EnsureSuccessStatusCode();

        var get = await client.GetAsync("/api/judge/config");
        get.EnsureSuccessStatusCode();
        var body = await get.Content.ReadAsStringAsync();

        Assert.Contains("\"enabled\":true", body);
        Assert.Contains("\"hasApiKey\":true", body);
        Assert.Contains("\"isConfigured\":true", body);
        Assert.Contains("\"timeoutSeconds\":12", body);
        Assert.Contains("judge-model-1", body);
        Assert.DoesNotContain("secret-judge-key", body);
    }

    [Fact]
    public async Task PutConfig_EmptyApiKey_KeepsStoredKey()
    {
        var client = _client!;
        var first = await client.PutAsJsonAsync("/api/judge/config", new
        {
            enabled = true,
            baseUrl = "https://ai.example.test/v1",
            model = "judge-model-1",
            apiKey = "original-secret-key",
            timeoutSeconds = 10
        });
        first.EnsureSuccessStatusCode();

        var second = await client.PutAsJsonAsync("/api/judge/config", new
        {
            enabled = true,
            baseUrl = "https://ai.example.test/v1",
            model = "judge-model-2",
            apiKey = "",
            timeoutSeconds = 15
        });
        second.EnsureSuccessStatusCode();

        var get = await client.GetAsync("/api/judge/config");
        get.EnsureSuccessStatusCode();
        var body = await get.Content.ReadAsStringAsync();

        Assert.Contains("\"hasApiKey\":true", body);
        Assert.Contains("judge-model-2", body);
        Assert.Contains("\"timeoutSeconds\":15", body);
        Assert.DoesNotContain("original-secret-key", body);
    }

    [Fact]
    public async Task Test_WhenBaseUrlMissing_ReturnsBadRequest()
    {
        var client = _client!;
        var res = await client.PostAsJsonAsync("/api/judge/test", new
        {
            baseUrl = ""
        });

        // If not configured and empty baseUrl given, should return 400 Bad Request
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Test_WithStubProvider_ReturnsProbeResult()
    {
        var listener = new HttpListener();
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stubTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch
                {
                    break;
                }

                if (ctx.Request.Url?.AbsolutePath.EndsWith("/chat/completions") == true)
                {
                    var responseJson = JsonSerializer.Serialize(new
                    {
                        choices = new[]
                        {
                            new { message = new { role = "assistant", content = "pong" } }
                        }
                    });
                    var buffer = System.Text.Encoding.UTF8.GetBytes(responseJson);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(buffer, cts.Token);
                    ctx.Response.Close();
                }
            }
        }, cts.Token);

        try
        {
            var client = _client!;
            var res = await client.PostAsJsonAsync("/api/judge/test", new
            {
                baseUrl = prefix,
                model = "test-model",
                apiKey = "test-key"
            });

            res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadAsStringAsync();
            Assert.Contains("\"ok\":true", body);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await stubTask; } catch { }
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        _client?.Dispose();
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}
