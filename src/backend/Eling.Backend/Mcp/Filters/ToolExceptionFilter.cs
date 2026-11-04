using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Filters;

/// <summary>
/// Catches unhandled exceptions from MCP tool invocations and translates them
/// into a standard <see cref="CallToolResult"/> with <c>IsError = true</c> and
/// the exception's message, preventing unhandled 500s / JSON-RPC faults on the transport.
/// </summary>
public static class ToolExceptionFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create()
    {
        return next => async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var toolName = context.Params?.Name ?? "unknown";
                var logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(ToolExceptionFilter))
                    ?? NullLogger.Instance;

                logger.LogError(ex, "Tool '{ToolName}' threw an unhandled exception.", toolName);

                return new CallToolResult
                {
                    IsError = true,
                    Content =
                    [
                        new TextContentBlock
                        {
                            Text = ex.Message
                        }
                    ]
                };
            }
        };
    }
}
