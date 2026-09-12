using System.Text.Json.Serialization;
using Eling.Core;
using Eling.Core.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Eling.Backend.Endpoints;

public static class MemoryMaintenanceEndpoints
{
    public static void MapMemoryMaintenanceEndpoints(this WebApplication app)
    {
        app.MapPost("/api/memory/maintenance", async Task<Results<Ok<MaintenanceReport>, BadRequest<string>>> (
            [FromBody] MaintenanceRequest request,
            [FromServices] IMemoryMaintenanceService maintenance,
            CancellationToken cancellationToken) =>
        {
            if (request.SimilarityThreshold < 0 || request.SimilarityThreshold > 1)
            {
                return TypedResults.BadRequest("similarityThreshold must be in [0, 1].");
            }
            if (request.StaleDays < 0)
            {
                return TypedResults.BadRequest("staleDays must be >= 0.");
            }

            var report = await maintenance.RunAsync(request, cancellationToken);
            return TypedResults.Ok(report);
        });
    }
}
