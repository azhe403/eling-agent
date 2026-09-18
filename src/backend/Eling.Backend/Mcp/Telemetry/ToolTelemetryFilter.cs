using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog.Context;

namespace Eling.Backend.Mcp.Telemetry;

/// <summary>
/// Filter untuk mencatat metrik eksekusi tool ke log terstruktur.
/// Mencatat: durasi (ms), tool name, payload request/response size, status, error.
/// </summary>
public static class ToolTelemetryFilter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Membuat filter untuk pipeline CallTool yang mencatat telemetry eksekusi.
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(ILogger? logger = null)
    {
        return next => async (context, cancellationToken) =>
        {
            var effectiveLogger = logger 
                ?? context.Services?.GetService<ILoggerFactory>()?.CreateLogger("Eling.Backend.Mcp.Telemetry")
                ?? NullLogger.Instance;

            return await ExecuteWithTelemetryAsync(context, next, effectiveLogger, cancellationToken);
        };
    }

    private static async Task<CallToolResult> ExecuteWithTelemetryAsync(
        RequestContext<CallToolRequestParams> context,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var request = context.Params;
        var toolName = request?.Name ?? "unknown";
        var requestId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N")[..12];
        var stopwatch = Stopwatch.StartNew();

        var requestPayload = JsonSerializer.Serialize(request, JsonOptions);
        var requestSize = requestPayload.Length;

        var correlationId = $"mcp-{requestId}";
        using var _ = LogContext.PushProperty("CorrelationId", correlationId);

        logger.LogDebug("ToolTelemetry: START tool={Tool} requestId={RequestId} requestSize={RequestSize} args={Args}",
            toolName, requestId, requestSize, TruncateForLog(request?.Arguments?.ToString() ?? "{}", 200));

        CallToolResult? result = null;
        string status = "success";
        string? errorMessage = null;
        string? errorCode = null;

        try
        {
            result = await next(context, cancellationToken);

            if (result?.IsError == true)
            {
                status = "tool_error";
                var firstBlock = result.Content?.FirstOrDefault();
                errorMessage = firstBlock is TextContentBlock textBlock ? textBlock.Text : "Tool returned error";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = "cancelled";
            errorMessage = "Operation cancelled";
            throw;
        }
        catch (Exception ex)
        {
            status = "exception";
            errorMessage = ex.Message;
            errorCode = ex.GetType().Name;

            logger.LogError(ex, "ToolTelemetry: EXCEPTION tool={Tool} requestId={RequestId} durationMs={DurationMs}",
                toolName, requestId, stopwatch.ElapsedMilliseconds);

            throw;
        }
        finally
        {
            stopwatch.Stop();

            var responsePayload = result is not null
                ? JsonSerializer.Serialize(result, JsonOptions)
                : "null";
            var responseSize = responsePayload.Length;

            logger.LogInformation(
                "ToolTelemetry: END tool={Tool} requestId={RequestId} status={Status} durationMs={DurationMs} requestSize={RequestSize} responseSize={ResponseSize} errorCode={ErrorCode} errorMessage={ErrorMessage}",
                toolName,
                requestId,
                status,
                stopwatch.ElapsedMilliseconds,
                requestSize,
                responseSize,
                errorCode ?? "none",
                errorMessage ?? "none");
        }

        return result!;
    }

    private static string TruncateForLog(string input, int maxLength)
    {
        if (string.IsNullOrEmpty(input) || input.Length <= maxLength)
            return input;
        
        return input[..maxLength] + "...[truncated]";
    }
}