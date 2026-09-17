using Eling.Backend.Agent.Ports;
using Eling.Backend.Agent.Services;
using Eling.Backend.Dtos;

namespace Eling.Backend.Endpoints;

public static class AgentEndpoints
{
    public static WebApplication MapAgentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/agent/provider");
        group.MapGet("/", (ProviderStore store) => TypedResults.Ok(store.GetView()));
        group.MapPut("/", (ProviderStore store, UpdateProviderRequest req) =>
        {
            store.Update(req.BaseUrl, req.Model, req.ApiKey);
            return TypedResults.Ok(store.GetView());
        });
        group.MapPost("/models", async (ProviderStore store, IProviderClient client, CancellationToken ct) =>
        {
            if (!store.TryGetConfig(out var baseUrl, out _, out var apiKey))
            {
                return Results.BadRequest("Provider is not configured.");
            }

            try
            {
                var models = await client.ListModelsAsync(baseUrl, apiKey, ct);
                store.SetModelsCached(models);
                return Results.Ok(new ModelListResponse([.. models]));
            }
            catch (Exception ex)
            {
                return Results.Problem(statusCode: 502, title: "Provider models fetch failed", detail: ex.Message);
            }
        });
        group.MapPost("/test", async (ProviderStore store, IProviderClient client, UpdateProviderRequest? req, CancellationToken ct) =>
        {
            var baseUrl = req?.BaseUrl;
            var model = req?.Model;
            string? apiKey = req?.ApiKey;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                if (!store.TryGetConfig(out var storedBase, out var storedModel, out var storedKey))
                {
                    return Results.BadRequest("Provider is not configured.");
                }

                baseUrl = storedBase;
                model ??= storedModel;
                if (string.IsNullOrEmpty(apiKey))
                {
                    apiKey = storedKey;
                }
            }

            var probe = await client.TestConnectionAsync(baseUrl, apiKey, model, ct);
            return Results.Ok(new ProviderTestResponse(probe.Ok, probe.Message));
        });
        return app;
    }
}
