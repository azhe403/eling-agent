using Eling.Backend.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Filters;

/// <summary>
/// MCP request filters that enforce the per-user tool policy stored by
/// <see cref="ToolPolicyStore"/>: disabled tools are removed from the
/// tools/list response and rejected at tools/call time with the standard
/// tool_disabled error, without ever invoking the tool handler.
/// </summary>
/// <remarks>
/// The store is resolved from the request services on every call, mirroring
/// how <c>ToolTelemetryFilter</c> resolves its logger, so a policy toggle
/// takes effect on the next request without restarting the server. When no
/// store is resolvable the filter fails open: an unresolvable dependency must
/// never brick every tool call.
/// </remarks>
public static class ToolPolicyFilters
{
    /// <summary>
    /// Creates the tools/list filter that strips disabled tools from the
    /// response the agent sees.
    /// </summary>
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> CreateListFilter()
    {
        return next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);

            var store = context.Services?.GetService<ToolPolicyStore>();
            if (store is null)
            {
                return result;
            }

            for (var i = result.Tools.Count - 1; i >= 0; i--)
            {
                if (store.IsToolDisabled(result.Tools[i].Name))
                {
                    result.Tools.RemoveAt(i);
                }
            }

            return result;
        };
    }

    /// <summary>
    /// Creates the tools/call filter that short-circuits disabled tools with
    /// an error result before the tool handler runs.
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CreateCallFilter()
    {
        return next => async (context, cancellationToken) =>
        {
            var store = context.Services?.GetService<ToolPolicyStore>();
            var toolName = context.Params?.Name;

            if (store is not null && store.IsToolDisabled(toolName))
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content =
                    [
                        new TextContentBlock
                        {
                            Text = $"tool_disabled: Tool '{toolName}' is currently disabled by user policy."
                        }
                    ]
                };
            }

            return await next(context, cancellationToken);
        };
    }
}
