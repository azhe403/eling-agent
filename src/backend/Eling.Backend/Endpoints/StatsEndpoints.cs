using Eling.Backend.Dtos;
using Eling.Core.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Eling.Backend.Endpoints;

/// <summary>
/// The dashboard's count-only read API.
/// </summary>
/// <remarks>
/// Separate from the memory list routes on purpose: a list endpoint answers
/// "give me these rows", and a dashboard that only wants a few numbers would
/// have to ask for every row to count them. The corpus grows with every save,
/// so that page would grow with it. This route answers the count question
/// directly and returns a payload that does not grow with the corpus.
/// </remarks>
public static class StatsEndpoints
{
    public static WebApplication MapStatsRoutes(this WebApplication app)
    {
        var stats = app.MapGroup("/api/stats");
        stats.MapGet("/memories", GetMemoryStatsAsync);
        return app;
    }

    /// <summary>
    /// How many memories there are, and how they break down.
    /// </summary>
    /// <remarks>
    /// The aggregation is the same one the memory list uses, so the numbers
    /// here and the rows on the Memories page always agree - including the
    /// fact that only alive workspaces are counted. A workspace whose process
    /// has exited is not in the aggregated list, so it is not in this count
    /// either.
    /// </remarks>
    private static async Task<Ok<MemoryStatsDto>> GetMemoryStatsAsync(RuntimeRegistry registry)
    {
        var all = await registry.ListAggregatedAsync();

        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        var byStatus = new Dictionary<string, int>(StringComparer.Ordinal);
        var byScope = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var scoped in all)
        {
            Count(byType, scoped.Memory.Type.ToString());
            Count(byStatus, scoped.Memory.Status.ToString());
            Count(byScope, MemoryScopeLabels.ToWireString(scoped.Scope));
        }

        return TypedResults.Ok(new MemoryStatsDto(all.Count, byType, byStatus, byScope));
    }

    private static void Count(Dictionary<string, int> counts, string key)
    {
        counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
    }
}
