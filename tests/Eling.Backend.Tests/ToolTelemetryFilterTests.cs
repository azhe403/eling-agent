using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Eling.Backend.Mcp.Telemetry;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Eling.Backend.Tests;

public class ToolTelemetryFilterTests
{
    private class TestLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }

    private static RequestContext<CallToolRequestParams> CreateRequestContext(string toolName, object? arguments = null)
    {
        var requestParams = new CallToolRequestParams
        {
            Name = toolName,
            Arguments = arguments is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments))
        };

        var context = (RequestContext<CallToolRequestParams>)RuntimeHelpers.GetUninitializedObject(typeof(RequestContext<CallToolRequestParams>));
        var field = typeof(RequestContext<CallToolRequestParams>).GetField("<Params>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(context, requestParams);

        return context;
    }

    [Fact]
    public async Task Filter_Logs_Success_Execution_With_Duration_And_Payload_Sizes()
    {
        var testLogger = new TestLogger();
        var filter = ToolTelemetryFilter.Create(testLogger);

        var requestContext = CreateRequestContext("memory_recall", new { query = "test query" });

        McpRequestHandler<CallToolRequestParams, CallToolResult> next = (ctx, ct) =>
        {
            Thread.Sleep(10); // simulate work
            var res = new CallToolResult
            {
                Content = [new TextContentBlock { Text = "recalled items" }]
            };
            return new ValueTask<CallToolResult>(res);
        };

        var handler = filter(next);
        var result = await handler(requestContext, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(testLogger.Logs, l => l.Level == LogLevel.Debug && l.Message.Contains("ToolTelemetry: START tool=memory_recall"));
        Assert.Contains(testLogger.Logs, l => l.Level == LogLevel.Information && l.Message.Contains("ToolTelemetry: END tool=memory_recall") && l.Message.Contains("status=success"));
    }

    [Fact]
    public async Task Filter_Logs_Tool_Error_Result()
    {
        var testLogger = new TestLogger();
        var filter = ToolTelemetryFilter.Create(testLogger);

        var requestContext = CreateRequestContext("memory_save", new { content = "" });

        McpRequestHandler<CallToolRequestParams, CallToolResult> next = (ctx, ct) =>
        {
            var res = new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "Content cannot be empty" }]
            };
            return new ValueTask<CallToolResult>(res);
        };

        var handler = filter(next);
        var result = await handler(requestContext, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Contains(testLogger.Logs, l => l.Level == LogLevel.Information && l.Message.Contains("status=tool_error") && l.Message.Contains("Content cannot be empty"));
    }

    [Fact]
    public async Task Filter_Logs_Exception_When_Tool_Throws()
    {
        var testLogger = new TestLogger();
        var filter = ToolTelemetryFilter.Create(testLogger);

        var requestContext = CreateRequestContext("file_read", new { path = "missing.txt" });

        McpRequestHandler<CallToolRequestParams, CallToolResult> next = (ctx, ct) =>
        {
            throw new FileNotFoundException("File missing");
        };

        var handler = filter(next);

        await Assert.ThrowsAsync<FileNotFoundException>(() => handler(requestContext, CancellationToken.None).AsTask());

        Assert.Contains(testLogger.Logs, l => l.Level == LogLevel.Error && l.Message.Contains("ToolTelemetry: EXCEPTION tool=file_read"));
        Assert.Contains(testLogger.Logs, l => l.Level == LogLevel.Information && l.Message.Contains("status=exception") && l.Message.Contains("FileNotFoundException"));
    }
}
