using System.Text.Json;
using Eling.Backend.Logging;
using Eling.Core.Logging;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Eling.Backend.Endpoints;

public static class LogsEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static WebApplication MapLogsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/logs");

        group.MapGet("/", (string? stream) =>
        {
            var s = string.IsNullOrWhiteSpace(stream) ? "backend" : stream.Trim();
            if (!LogReader.IsValidStream(s))
            {
                return Results.BadRequest($"Unknown stream '{s}'.");
            }
            var dir = CentralLogDirectory.Resolve();
            var files = LogReader.ListFiles(dir, s);
            return TypedResults.Ok(new { directory = dir, count = files.Count, files });
        });

        group.MapGet("/read", (string? stream, string? date, string? level, string? contains, int? tail) =>
        {
            var s = string.IsNullOrWhiteSpace(stream) ? "backend" : stream.Trim();
            if (!LogReader.IsValidStream(s))
            {
                return Results.BadRequest($"Unknown stream '{s}'.");
            }
            if (!LogReader.IsValidDate(date))
            {
                return Results.BadRequest($"Invalid date '{date}'. Use YYYY-MM-DD.");
            }

            var dir = CentralLogDirectory.Resolve();
            var result = LogReader.Read(dir, s, date, level, contains, tail ?? 500);
            if (!result.Exists)
            {
                return Results.NotFound(new { ok = false, error = $"Log file not found: {result.File}", code = "not_found", directory = dir });
            }
            return TypedResults.Ok(new
            {
                file = result.File,
                directory = result.Directory,
                totalLines = result.TotalLines,
                filteredTotal = result.FilteredTotal,
                returned = result.Returned,
                lines = result.Lines
            });
        });

        group.MapGet("/tail", async (HttpContext ctx, string? stream, string? level, string? contains, int? tail) =>
        {
            var s = string.IsNullOrWhiteSpace(stream) ? "backend" : stream.Trim();
            if (!LogReader.IsValidStream(s))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync($"Unknown stream '{s}'.", ctx.RequestAborted);
                return;
            }

            var dir = CentralLogDirectory.Resolve();
            var path = LogReader.ResolvePath(dir, s, null); // tail active file only
            var initialTail = Math.Clamp(tail ?? 50, 1, 5000);

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var ct = ctx.RequestAborted;
            long position = 0;

            // Initial backlog: last N matching lines from current file.
            if (File.Exists(path))
            {
                var backlog = LogReader.ReadTailChunk(path, level, contains, initialTail);
                foreach (var line in backlog)
                {
                    await WriteEvent(ctx, line, ct);
                }
                position = LogReader.FileSize(path);
            }

            var lastHeartbeat = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);

                if (!File.Exists(path))
                {
                    position = 0;
                    continue;
                }

                var newLines = LogReader.ReadNewLines(path, ref position, level, contains);
                foreach (var line in newLines)
                {
                    await WriteEvent(ctx, line, ct);
                }

                if ((DateTime.UtcNow - lastHeartbeat).TotalSeconds >= 15)
                {
                    await ctx.Response.WriteAsync(": ping\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    lastHeartbeat = DateTime.UtcNow;
                }
            }
        });

        group.MapGet("/config", (LogConfigStore store) =>
        {
            return Results.Ok(new { format = store.GetFormat().ToString().ToLowerInvariant() });
        });

        group.MapPut("/config", (LogConfigStore store, LogFormatSwitcher switcher, LogConfigRequest req) =>
        {
            var parsed = LogFormatExtensions.Resolve(req?.Format);
            // Persist FIRST, then apply the live effect: a host that crashes
            // mid-PUT still restarts with the stored value.
            var updated = store.UpdateFormat(parsed);
            switcher.Apply(updated);
            return Results.Ok(new { format = updated.ToString().ToLowerInvariant() });
        });

        return app;
    }

    /// <summary>
    /// The per-user log format view; the wire shape is always a lowercase
    /// string so a missing/unknown value in the request body resolves
    /// through <see cref="LogFormatExtensions.Resolve"/> instead of a 400.
    /// </summary>
    public sealed record LogConfigRequest(string? Format);

    private static async Task WriteEvent(HttpContext ctx, string line, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { line }, JsonOptions);
        await ctx.Response.WriteAsync($"data: {payload}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
}
