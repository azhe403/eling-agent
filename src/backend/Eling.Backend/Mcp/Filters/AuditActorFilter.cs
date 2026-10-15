using Eling.Core.Audit;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Filters;

/// <summary>
/// Opens the ambient audit actor for the duration of every MCP tool call, so
/// the entries the memory domain records underneath carry the calling client
/// (design §5). The name comes from the <c>ELING_MCP_SERVER_NAME</c>
/// environment variable a host entry supplies (amendment A3): one binary serves
/// every client, so the client-chosen server name is invisible to the host
/// without it. An unset or blank name falls back to the plain <c>mcp</c>
/// actor rather than spelling <c>mcp:mcp</c>; the source is always
/// <c>mcp_stdio</c>.
/// </summary>
/// <remarks>
/// Registered between the telemetry and policy filters (see
/// <see cref="McpServiceExtensions.AddElingMcpServerStdio"/>): the scope opens
/// before the policy check, so even a rejected call carries the actor it was
/// made as, and telemetry stays outermost and keeps logging blocked calls.
/// </remarks>
public static class AuditActorFilter
{
    public const string ServerNameVariable = "ELING_MCP_SERVER_NAME";

    private const string FallbackActor = "mcp";

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create()
    {
        var actor = new AuditActor(ResolveActorName(), "mcp_stdio");

        return next => async (context, cancellationToken) =>
        {
            // One scope per call, disposed when the call returns: the ambient
            // actor is AsyncLocal, so nothing leaks into a later call.
            using var scope = AuditActorContext.Begin(actor);
            return await next(context, cancellationToken);
        };
    }

    /// <summary>
    /// The actor name for this host entry, resolved once at registration:
    /// <c>mcp:&lt;server-name&gt;</c> when the variable is set, <c>mcp</c> when
    /// it is not (amendment A3).
    /// </summary>
    public static string ResolveActorName()
    {
        var serverName = Environment.GetEnvironmentVariable(ServerNameVariable);
        return string.IsNullOrWhiteSpace(serverName)
            ? FallbackActor
            : $"mcp:{serverName.Trim()}";
    }
}
