using Eling.Backend.Agent.Ports;
using Eling.Backend.Dtos;
using Eling.Backend.Judging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Eling.Backend.Endpoints;

public static class JudgeEndpoints
{
    public static WebApplication MapJudgeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/judge");

        group.MapGet("/config", (SemanticJudgeStore store) => TypedResults.Ok(store.GetView()));

        group.MapPut("/config", (SemanticJudgeStore store, UpdateJudgeConfigRequest req) =>
        {
            var updated = store.Update(req.Enabled, req.BaseUrl, req.Model, req.ApiKey, req.TimeoutSeconds);
            return TypedResults.Ok(updated);
        });

        group.MapPost("/models", async (SemanticJudgeStore store, IProviderClient client, UpdateJudgeConfigRequest? req, CancellationToken ct) =>
        {
            var baseUrl = req?.BaseUrl;
            var apiKey = req?.ApiKey;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                if (!store.TryGetConfig(out var storedBase, out _, out var storedKey, out _))
                {
                    return Results.BadRequest("Judge provider is not configured.");
                }

                baseUrl = storedBase;
                apiKey = storedKey;
            }
            else if (string.IsNullOrEmpty(apiKey))
            {
                store.TryGetConfig(out _, out _, out var storedKey, out _);
                apiKey = storedKey;
            }

            try
            {
                var models = await client.ListModelsAsync(baseUrl, apiKey, ct);
                return Results.Ok(new ModelListResponse([.. models]));
            }
            catch (Exception ex)
            {
                return Results.Problem(statusCode: 502, title: "Judge provider models fetch failed", detail: ex.Message);
            }
        });

        group.MapPost("/test", async (SemanticJudgeStore store, IProviderClient client, UpdateJudgeConfigRequest? req, CancellationToken ct) =>
        {
            var baseUrl = req?.BaseUrl;
            var model = req?.Model;
            var apiKey = req?.ApiKey;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                var view = store.GetView();
                baseUrl = view.BaseUrl;
                model ??= view.Model;
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                store.TryGetConfig(out _, out _, out var storedKey, out _);
                apiKey = storedKey;
            }

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return Results.BadRequest("Base URL is required to test judge provider.");
            }

            var probe = await client.TestConnectionAsync(baseUrl, apiKey, model, ct);
            return Results.Ok(new ProviderTestResponse(probe.Ok, probe.Message));
        });

        return app;
    }
}
