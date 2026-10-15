using System.Reflection;
using System.Runtime.CompilerServices;
using Eling.Backend.Mcp.Filters;
using Eling.Core.Audit;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The ambient audit actor for MCP tool calls (design §5, amendment A3): the
/// filter opens the scope for the whole call, and it dissolves afterwards, so
/// nothing leaks into a later call on the same session.
/// </summary>
public sealed class AuditActorFilterTests
{
    private static RequestContext<CallToolRequestParams> CreateRequestContext(string toolName)
    {
        var requestParams = new CallToolRequestParams { Name = toolName };
        var context = (RequestContext<CallToolRequestParams>)RuntimeHelpers.GetUninitializedObject(typeof(RequestContext<CallToolRequestParams>));
        var field = typeof(RequestContext<CallToolRequestParams>).GetField("<Params>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(context, requestParams);
        return context;
    }

    [Fact]
    public async Task Filter_OpensTheAmbientActorForTheWholeCall()
    {
        var filter = AuditActorFilter.Create();
        AuditActor? seen = null;

        McpRequestHandler<CallToolRequestParams, CallToolResult> next = (context, cancellationToken) =>
        {
            // The actor the service-level hooks would read at record time.
            seen = AuditActorContext.Current;
            return new ValueTask<CallToolResult>(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "done" }]
            });
        };

        await filter(next)(CreateRequestContext("memory_save"), CancellationToken.None);

        Assert.NotNull(seen);
        // ELING_MCP_SERVER_NAME is unset here, so the host entry's actor is the
        // plain "mcp" with the mcp_stdio source (amendment A3). The other test
        // pins the named form.
        Assert.Equal("mcp", seen!.Actor);
        Assert.Equal("mcp_stdio", seen.Source);
    }

    [Fact]
    public async Task Filter_DissolvesTheScopeWhenTheCallReturns()
    {
        var filter = AuditActorFilter.Create();
        var before = AuditActorContext.Current;

        McpRequestHandler<CallToolRequestParams, CallToolResult> next = (context, cancellationToken) =>
            new ValueTask<CallToolResult>(new CallToolResult());

        await filter(next)(CreateRequestContext("memory_search"), CancellationToken.None);

        // An AsyncLocal scope must not outlive its call: the next call on the
        // same session must not inherit this one's actor.
        Assert.Equal(before, AuditActorContext.Current);
    }

    [Fact]
    public void ActorName_UsesTheServerNameVariable_AndFallsBackToMcp()
    {
        var original = Environment.GetEnvironmentVariable(AuditActorFilter.ServerNameVariable);
        try
        {
            Environment.SetEnvironmentVariable(AuditActorFilter.ServerNameVariable, "eling_dev");
            Assert.Equal("mcp:eling_dev", AuditActorFilter.ResolveActorName());

            Environment.SetEnvironmentVariable(AuditActorFilter.ServerNameVariable, "  spaced  ");
            Assert.Equal("mcp:spaced", AuditActorFilter.ResolveActorName());

            // One binary serves every client, so an unset name is the normal
            // case: the plain "mcp" actor, never "mcp:mcp" (amendment A3).
            Environment.SetEnvironmentVariable(AuditActorFilter.ServerNameVariable, null);
            Assert.Equal("mcp", AuditActorFilter.ResolveActorName());

            Environment.SetEnvironmentVariable(AuditActorFilter.ServerNameVariable, "   ");
            Assert.Equal("mcp", AuditActorFilter.ResolveActorName());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditActorFilter.ServerNameVariable, original);
        }
    }
}
