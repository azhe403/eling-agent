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

                if (ex is Eling.Core.Exceptions.InvalidProjectTargetException invalid)
                {
                    // Expected caller-input validation failure (unknown
                    // ancestor scope): short warning, no stack trace.
                    logger.LogWarning("Tool '{ToolName}' rejected: {Message}", toolName, invalid.Message);
                }
                else if (IsExpectedToolFailure(ex))
                {
                    // Expected caller/environment failures (validation, DB
                    // down, unreachable host, unknown column, server-level
                    // connection without a database): keep the line, drop
                    // the stack trace and the Error level. Genuinely
                    // unexpected exceptions stay Error.
                    logger.LogWarning("Tool '{ToolName}' rejected: {Message}", toolName, ex.Message);
                }
                else
                {
                    logger.LogError(ex, "Tool '{ToolName}' threw an unhandled exception.", toolName);
                }

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

    /// <summary>
    /// True for failures the caller can fix or that the read path already
    /// absorbs (validation, unreachable/down database, connection not found,
    /// rejected SQL). Walks the whole exception chain: the tools wrap the
    /// provider's exception in an InvalidOperationException, and the provider
    /// exception carries the meaningful category.
    /// </summary>
    private static bool IsExpectedToolFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is Eling.Core.Skill.SkillValidationException
                || e is Eling.Core.Database.DatabaseUnavailableException
                || e is KeyNotFoundException
                || e is TimeoutException
                || e is System.Net.Sockets.SocketException)
            {
                return true;
            }

            // Provider exceptions (MySqlException, PostgresException,
            // SqliteException, Redis*-Exception) carry the meaningful
            // category (bad SQL, no database selected, host down).
            var typeName = e.GetType().Name;
            if (typeName.EndsWith("Exception", StringComparison.Ordinal)
                && (typeName.StartsWith("MySql", StringComparison.OrdinalIgnoreCase)
                    || typeName.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase)
                    || typeName.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase)
                    || typeName.StartsWith("Sqlite", StringComparison.OrdinalIgnoreCase)
                    || typeName.StartsWith("Redis", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        // Wrapper messages the tools raise for calls rejected before execution.
        if (ex is InvalidOperationException io
            && (io.Message.StartsWith("query failed", StringComparison.Ordinal)
                || io.Message.StartsWith("SQL rejected", StringComparison.Ordinal)
                || io.Message.StartsWith("connection is server-level", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }
}
