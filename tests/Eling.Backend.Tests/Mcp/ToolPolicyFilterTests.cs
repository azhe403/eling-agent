using System.Reflection;
using System.Runtime.CompilerServices;
using Eling.Backend.Mcp.Filters;
using Eling.Backend.Tools;
using Eling.Core.Scope;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Eling.Backend.Tests.Mcp;

/// <summary>
/// Verifies the policy enforcement filters: disabled tools disappear from
/// tools/list and are rejected at tools/call time with the standard
/// tool_disabled error, while enabled and protected tools pass through. The
/// store is resolved from the request services, matching the telemetry filter,
/// so a missing store fails open and never bricks the server.
/// </summary>
public sealed class ToolPolicyFilterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"eling-tools-filters-{Guid.NewGuid():N}");

    private ToolPolicyStore NewStore()
    {
        Directory.CreateDirectory(_root);
        return new ToolPolicyStore(new UserScope(_root), NullLogger<ToolPolicyStore>.Instance);
    }

    private static ServiceProvider NewProvider(ToolPolicyStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task CallFilter_DisabledTool_ReturnsToolDisabledErrorWithoutInvokingNext()
    {
        var store = NewStore();
        store.DisableTools(["file_search"]);
        var nextInvoked = false;

        McpRequestHandler<CallToolRequestParams, CallToolResult> next = (context, cancellationToken) =>
        {
            nextInvoked = true;
            return new ValueTask<CallToolResult>(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "handler should not run" }]
            });
        };

        var handler = ToolPolicyFilters.CreateCallFilter()(next);
        var result = await handler(CreateCallContext("file_search", NewProvider(store)), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.False(nextInvoked, "the tool handler must not run for a disabled tool");
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        Assert.Equal("tool_disabled: Tool 'file_search' is currently disabled by user policy.", text);
    }

    [Fact]
    public async Task CallFilter_EnabledTool_InvokesNextAndPassesResultThrough()
    {
        var store = NewStore();
        var nextInvoked = false;

        McpRequestHandler<CallToolRequestParams, CallToolResult> next = (context, cancellationToken) =>
        {
            nextInvoked = true;
            return new ValueTask<CallToolResult>(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "handler ran" }]
            });
        };

        var handler = ToolPolicyFilters.CreateCallFilter()(next);
        var result = await handler(CreateCallContext("file_search", NewProvider(store)), CancellationToken.None);

        Assert.True(nextInvoked);
        Assert.True(result.IsError is null or false, "an enabled tool must not be reported as an error");
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        Assert.Equal("handler ran", text);
    }

    [Fact]
    public async Task CallFilter_ProtectedTool_IsNeverBlocked()
    {
        var store = NewStore();
        store.DisableTools(["tools_policy", "memory_recall"]);
        var nextInvoked = false;

        McpRequestHandler<CallToolRequestParams, CallToolResult> next = (context, cancellationToken) =>
        {
            nextInvoked = true;
            return new ValueTask<CallToolResult>(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "protected ran" }]
            });
        };

        var handler = ToolPolicyFilters.CreateCallFilter()(next);
        var result = await handler(CreateCallContext("tools_policy", NewProvider(store)), CancellationToken.None);

        Assert.True(nextInvoked);
        Assert.True(result.IsError is null or false, "a protected tool must not be blocked");
        Assert.False(store.IsToolDisabled("tools_policy"));
    }

    [Fact]
    public async Task CallFilter_MissingStore_FailsOpenAndInvokesNext()
    {
        var nextInvoked = false;

        McpRequestHandler<CallToolRequestParams, CallToolResult> next = (context, cancellationToken) =>
        {
            nextInvoked = true;
            return new ValueTask<CallToolResult>(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "ran without store" }]
            });
        };

        var handler = ToolPolicyFilters.CreateCallFilter()(next);
        var result = await handler(CreateCallContext("file_search", services: null), CancellationToken.None);

        Assert.True(nextInvoked);
        Assert.True(result.IsError is null or false, "a missing store must fail open");
    }

    [Fact]
    public async Task ListFilter_RemovesDisabledToolsAndKeepsTheRest()
    {
        var store = NewStore();
        store.DisableTools(["file_delete", "memory_delete"]);

        var listResult = new ListToolsResult
        {
            Tools = new List<Tool>
            {
                new() { Name = "file_read" },
                new() { Name = "file_delete" },
                new() { Name = "file_search" },
                new() { Name = "memory_delete" },
                new() { Name = "memory_recall" },
            }
        };

        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next = (context, cancellationToken) =>
            new ValueTask<ListToolsResult>(listResult);

        var handler = ToolPolicyFilters.CreateListFilter()(next);
        var result = await handler(CreateListContext(NewProvider(store)), CancellationToken.None);

        Assert.Equal(3, result.Tools.Count);
        Assert.Contains(result.Tools, tool => tool.Name == "file_read");
        Assert.Contains(result.Tools, tool => tool.Name == "file_search");
        Assert.Contains(result.Tools, tool => tool.Name == "memory_recall");
        Assert.DoesNotContain(result.Tools, tool => tool.Name == "file_delete");
        Assert.DoesNotContain(result.Tools, tool => tool.Name == "memory_delete");
    }

    [Fact]
    public async Task ListFilter_MissingStore_LeavesListUnchanged()
    {
        var listResult = new ListToolsResult
        {
            Tools = new List<Tool>
            {
                new() { Name = "file_read" },
                new() { Name = "file_delete" },
            }
        };

        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next = (context, cancellationToken) =>
            new ValueTask<ListToolsResult>(listResult);

        var handler = ToolPolicyFilters.CreateListFilter()(next);
        var result = await handler(CreateListContext(services: null), CancellationToken.None);

        Assert.Equal(2, result.Tools.Count);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static RequestContext<CallToolRequestParams> CreateCallContext(
        string toolName,
        IServiceProvider? services)
        => CreateContext<CallToolRequestParams>(
            new CallToolRequestParams { Name = toolName },
            services);

    private static RequestContext<ListToolsRequestParams> CreateListContext(IServiceProvider? services)
        => CreateContext<ListToolsRequestParams>(null, services);

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
