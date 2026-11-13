using System.Reflection;
using System.Runtime.CompilerServices;
using Eling.Backend.Mcp.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Eling.Backend.Tests.Mcp;

public sealed class ToolExceptionFilterTests
{
    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<string> LoggedMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LoggedMessages.Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task CallFilter_WhenNextThrows_CatchesAndReturnsIsErrorWithExceptionMessage()
    {
        var logger = new TestLogger<ToolExceptionFilterTests>();
        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(new LoggerFactory([new TestLoggerProvider(logger)]))
            .BuildServiceProvider();

        McpRequestHandler<CallToolRequestParams, CallToolResult> next = (_, _) =>
            throw new ArgumentException("Content cannot be empty.");

        var filter = ToolExceptionFilter.Create();
        var handler = filter(next);
        var context = CreateCallContext("memory_save", services);

        var result = await handler(context, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsError);
        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!));
        Assert.Equal("Content cannot be empty.", block.Text);
        Assert.NotEmpty(logger.LoggedMessages);
    }

    [Fact]
    public async Task CallFilter_WhenNextSucceeds_PassesResultThroughUnmodified()
    {
        McpRequestHandler<CallToolRequestParams, CallToolResult> next = (_, _) =>
            new ValueTask<CallToolResult>(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "success" }]
            });

        var filter = ToolExceptionFilter.Create();
        var handler = filter(next);
        var context = CreateCallContext("recall", services: null);

        var result = await handler(context, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsError ?? false);
        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!));
        Assert.Equal("success", block.Text);
    }

    [Fact]
    public async Task CallFilter_WhenOperationCanceledExceptionThrown_RethrowsToAllowCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        McpRequestHandler<CallToolRequestParams, CallToolResult> next = (_, ct) =>
            throw new OperationCanceledException(ct);

        var filter = ToolExceptionFilter.Create();
        var handler = filter(next);
        var context = CreateCallContext("file_search", services: null);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await handler(context, cts.Token);
        });
    }

    private sealed class TestLoggerProvider : ILoggerProvider
    {
        private readonly ILogger _logger;

        public TestLoggerProvider(ILogger logger) => _logger = logger;

        public ILogger CreateLogger(string categoryName) => _logger;

        public void Dispose() { }
    }

    private static RequestContext<CallToolRequestParams> CreateCallContext(
        string toolName,
        IServiceProvider? services)
        => CreateContext(new CallToolRequestParams { Name = toolName }, services);

    private static RequestContext<TParams> CreateContext<TParams>(TParams? requestParams, IServiceProvider? services)
    {
        var context = (RequestContext<TParams>)RuntimeHelpers.GetUninitializedObject(typeof(RequestContext<TParams>));
        SetMember(context, "Params", requestParams);
        SetMember(context, "Services", services);
        return context;
    }

    private static void SetMember<TParams>(RequestContext<TParams> context, string memberName, object? value)
    {
        var setter = typeof(RequestContext<TParams>)
            .GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance)
            ?.GetSetMethod(nonPublic: true);
        if (setter is not null)
        {
            setter.Invoke(context, [value]);
            return;
        }

        var backingField = typeof(RequestContext<TParams>)
            .GetField($"<{memberName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? typeof(RequestContext<TParams>).BaseType?.GetField(
                $"<{memberName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        backingField?.SetValue(context, value);
    }
}
