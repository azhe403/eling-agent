using System.Net;
using System.Text;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Tests;

/// <summary>
/// The envelope middleware is the one place that guarantees (1) no zero-byte
/// JSON response except on 204/304/HEAD and (2) every JSON body carries
/// { code, message, data } — errors and all. These tests pin both guarantees.
/// </summary>
public sealed class ApiEnvelopeTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.UseMiddleware<ApiEnvelopeMiddleware>();

        _app.MapMethods("/envelope/ok", new[] { "GET", "HEAD" }, () => Results.Ok(new { hello = "world" }));
        _app.MapGet("/envelope/array", () => Results.Ok(new[] { "a", "b" }));
        _app.MapGet("/envelope/notfound", () => Results.NotFound());
        _app.MapGet("/envelope/domain-code", (HttpContext context) =>
        {
            context.Items[ApiEnvelopeMiddleware.ErrorCodeItem] = "connection_not_found";
            return Results.NotFound();
        });
        _app.MapGet("/envelope/boom", async () => { throw new InvalidOperationException("boom"); });
        _app.MapGet("/envelope/text-error", async (HttpContext context) =>
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Unknown stream 'foo'.");
        });
        _app.MapPost("/envelope/ok-post", () => Results.Ok());
        _app.MapDelete("/envelope/no-content", () => Results.NoContent());
        _app.MapGet("/envelope/sse", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: connected\n\n");
            await context.Response.Body.FlushAsync();
        });
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(raw);
    }

    private static void AssertKeyOrder(string raw)
    {
        var code = raw.IndexOf("\"code\"", StringComparison.Ordinal);
        var message = raw.IndexOf("\"message\"", StringComparison.Ordinal);
        var data = raw.IndexOf("\"data\"", StringComparison.Ordinal);
        Assert.True(code >= 0 && message > code && data > message, $"key order wrong: {raw}");
    }

    [Fact]
    public async Task Get_ObjectPayload_IsEnveloped_WithCodeOk()
    {
        var res = await _client.GetAsync("/envelope/ok");
        res.EnsureSuccessStatusCode();
        var raw = await res.Content.ReadAsStringAsync();
        AssertKeyOrder(raw);
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("ok", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("The resource was read successfully from the server.", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("world", doc.RootElement.GetProperty("data").GetProperty("hello").GetString());
    }

    [Fact]
    public async Task Get_ArrayPayload_RoundTripsAsData()
    {
        var res = await _client.GetAsync("/envelope/array");
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Equal(2, data.GetArrayLength());
        Assert.Equal("a", data[0].GetString());
        Assert.Equal("b", data[1].GetString());
    }

    [Fact]
    public async Task BareNotFound_Answers404_WithGenericCode()
    {
        var res = await _client.GetAsync("/envelope/notfound");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("data").ValueKind);
        Assert.True(doc.RootElement.GetProperty("message").GetString()!.Length >= 6);
    }

    [Fact]
    public async Task EndpointSettingErrorCode_TravelsInRootCode()
    {
        var res = await _client.GetAsync("/envelope/domain-code");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("connection_not_found", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task EndpointThatThrows_Answers500_InternalError()
    {
        var res = await _client.GetAsync("/envelope/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("internal_error", doc.RootElement.GetProperty("code").GetString());
        Assert.Contains("boom", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task TextPlainErrorBody_LandsInMessage()
    {
        var res = await _client.GetAsync("/envelope/text-error");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/json; charset=utf-8", res.Content.Headers.ContentType?.ToString());
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("bad_request", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("Unknown stream 'foo'.", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task EmptyTwoHundred_IsOkAndVerbDerived()
    {
        var res = await _client.PostAsync("/envelope/ok-post", content: null);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("The request was processed successfully by the server.", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task NoContent_204_NeverHasBody()
    {
        var res = await _client.DeleteAsync("/envelope/no-content");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal(string.Empty, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sse_Route_EmitsFramesUnbuffered()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/envelope/sse");
        using var res = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[64];
        var total = 0;
        while (total == 0 || !Encoding.UTF8.GetString(buffer, 0, total).Contains("data: connected", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, 64 - total), cts.Token);
            if (read == 0) break;
            total += read;
            if (total >= buffer.Length - 1) break;
        }
        cts.Cancel();
        Assert.Contains("data: connected", Encoding.UTF8.GetString(buffer, 0, total));
    }

    [Fact]
    public async Task Head_Route_ReturnsZeroBytes()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, "/envelope/ok");
        var res = await _client.SendAsync(request);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsByteArrayAsync();
        Assert.Empty(body);
    }
}
