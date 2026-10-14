using Eling.Backend.Bootstrap;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Telemetry;

/// <summary>
/// Marks the startup stopwatch the first time the client lists tools — the
/// closest in-process proxy for "the client considers this server connected"
/// (initialize handshake done, catalog fetched). Emits the phase-breakdown
/// summary once per process lifetime.
/// </summary>
public static class StartupTimingFilter
{
    /// <summary>
    /// Creates the tools/list filter that records the MCP-ready milestone.
    /// </summary>
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> Create()
    {
        return next => (context, cancellationToken) =>
        {
            StartupStopwatch.MarkMcpReady();

            return next(context, cancellationToken);
        };
    }
}
