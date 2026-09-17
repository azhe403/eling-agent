using Eling.Backend.Agent.Infrastructure.Ai;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Agent.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

public class MeaiGatewayTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<WebApplication> _stubs = [];
    private string _capturedBody = string.Empty;

    public MeaiGatewayTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-meai-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ToolCallResponse_MapsToToolCallRequest_AndSendsTools()
    {
        var port = await StartStubAsync(
            """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"a.txt\"}"}}]}}]}""");

        var store = new ProviderStore(_tempDir, NullLogger<ProviderStore>.Instance);
        store.Update($"http://127.0.0.1:{port}", "stub-model", null);
        var gateway = new MeaiChatGateway(store, NullLogger<MeaiChatGateway>.Instance);

        var result = await gateway.CompleteAsync(new SingleShotRequest(
            "stub-model",
            [new AgentMessage(AgentRole.User, "read a.txt")],
            [new ToolDefinition("read_file", "Read a file", """{"type":"object"}""")]),
            CancellationToken.None);

        var toolCall = Assert.Single(result.ToolCalls);
        Assert.Equal("read_file", toolCall.Name);
        Assert.Contains("tools", _capturedBody);
        Assert.Contains("tool_choice", _capturedBody);
    }

    [Fact]
    public async Task PlainTextResponse_MapsToAssistantText()
    {
        var port = await StartStubAsync(
            """{"choices":[{"message":{"role":"assistant","content":"hi"}}]}""");

        var store = new ProviderStore(_tempDir, NullLogger<ProviderStore>.Instance);
        store.Update($"http://127.0.0.1:{port}", "stub-model", null);
        var gateway = new MeaiChatGateway(store, NullLogger<MeaiChatGateway>.Instance);

        var result = await gateway.CompleteAsync(new SingleShotRequest(
            "stub-model",
            [new AgentMessage(AgentRole.User, "hello")],
            []),
            CancellationToken.None);

        Assert.Equal("hi", result.AssistantText);
        Assert.Empty(result.ToolCalls);
    }

    [Fact]
    public async Task StreamingText_DeltasArriveIncrementally()
    {
        var port = await StartSseStubAsync(
            "data: {\"id\":\"1\",\"choices\":[{\"delta\":{\"content\":\"he\"},\"index\":0}]}\n\ndata: {\"id\":\"1\",\"choices\":[{\"delta\":{\"content\":\"llo\"},\"index\":0}]}\n\ndata: [DONE]\n\n");

        var store = new ProviderStore(_tempDir, NullLogger<ProviderStore>.Instance);
        store.Update($"http://127.0.0.1:{port}", "stub-model", null);
        var gateway = new MeaiChatGateway(store, NullLogger<MeaiChatGateway>.Instance);

        var events = new List<ChatStreamEvent>();
        await foreach (var ev in gateway.CompleteStreamingAsync(new SingleShotRequest(
            "stub-model",
            [new AgentMessage(AgentRole.User, "hello")],
            []),
            CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Equal(["he", "llo"], events.OfType<ChatTextDelta>().Select(d => d.Delta));
    }

    [Fact]
    public async Task StreamingToolCall_MapsToToolRequest()
    {
        var port = await StartSseStubAsync(
            "data: {\"id\":\"1\",\"choices\":[{\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}}]},\"index\":0,\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n");

        var store = new ProviderStore(_tempDir, NullLogger<ProviderStore>.Instance);
        store.Update($"http://127.0.0.1:{port}", "stub-model", null);
        var gateway = new MeaiChatGateway(store, NullLogger<MeaiChatGateway>.Instance);

        var events = new List<ChatStreamEvent>();
        await foreach (var ev in gateway.CompleteStreamingAsync(new SingleShotRequest(
            "stub-model",
            [new AgentMessage(AgentRole.User, "read a.txt")],
            [new ToolDefinition("read_file", "Read a file", """{"type":"object"}""")]),
            CancellationToken.None))
        {
            events.Add(ev);
        }

        var toolCall = Assert.Single(events.OfType<ChatToolRequest>());
        Assert.Equal("read_file", toolCall.Call.Name);
        Assert.Contains("tools", _capturedBody);
    }

    private async Task<int> StartStubAsync(string responseJson)
    {
        return await StartRawStubAsync(responseJson, "application/json");
    }

    private async Task<int> StartSseStubAsync(string sseBody)
    {
        return await StartRawStubAsync(sseBody, "text/event-stream");
    }

    private async Task<int> StartRawStubAsync(string body, string contentType)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var stub = builder.Build();
        stub.MapPost("/chat/completions", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            _capturedBody = await reader.ReadToEndAsync();
            return Results.Content(body, contentType);
        });
        await stub.StartAsync();
        _stubs.Add(stub);
        return port;
    }

    public void Dispose()
    {
        foreach (var stub in _stubs)
        {
            try
            {
                stub.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
