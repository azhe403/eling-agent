using Eling.Backend.Dtos;
using Eling.Backend.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Eling.Backend.Endpoints;

public static class ToolsEndpoints
{
    public static WebApplication MapToolsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tools");

        group.MapGet("/", (ToolPolicyStore store) => TypedResults.Ok(ListTools(store)));

        group.MapPut("/", (ToolPolicyStore store, UpdateToolPolicyRequest req) =>
        {
            if (!string.IsNullOrWhiteSpace(req.ToolName))
            {
                var name = req.ToolName.Trim();
                if (!ToolCatalog.Exists(name))
                {
                    return Results.BadRequest($"Unknown tool '{name}'.");
                }

                if (!req.Enabled && ToolPolicyStore.IsProtected(name))
                {
                    return Results.BadRequest($"Tool '{name}' is protected and cannot be disabled.");
                }

                if (req.Enabled)
                {
                    store.EnableTools([name]);
                }
                else
                {
                    store.DisableTools([name]);
                }

                return TypedResults.Ok(ListTools(store));
            }

            if (!string.IsNullOrWhiteSpace(req.Group))
            {
                var members = ToolCatalog.MembersOf(req.Group.Trim());
                if (members.Count == 0)
                {
                    return Results.BadRequest($"Unknown tool group '{req.Group}'.");
                }

                if (req.Enabled)
                {
                    store.EnableTools(members);
                }
                else
                {
                    store.DisableTools(members);
                }

                return TypedResults.Ok(ListTools(store));
            }

            return Results.BadRequest("Provide either 'toolName' or 'group'.");
        });

        return app;
    }

    private static List<ToolItemDto> ListTools(ToolPolicyStore store)
        => ToolCatalog.All
            .Select(tool => new ToolItemDto(
                tool.Name,
                tool.Group,
                tool.Description,
                !store.IsToolDisabled(tool.Name),
                ToolPolicyStore.IsProtected(tool.Name)))
            .ToList();
}
