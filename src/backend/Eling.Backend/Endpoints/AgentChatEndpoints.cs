using System.Text.Json;
using Eling.Backend.Agent.Services;
using Eling.Backend.Dtos;
using Eling.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Endpoints;

public static class AgentChatEndpoints
{
    private static JsonSerializerOptions StreamJsonOptions => JsonDefaults.Shared;

    public static WebApplication MapAgentChatEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Agent.Chat");

        var chats = app.MapGroup("/api/agent/chats");
        chats.MapGet("/", (BackendChatStore store, string workspace) =>
            TypedResults.Ok(store.List(workspace)));
        chats.MapGet("/{id}", (BackendChatStore store, string id) =>
        {
            var chat = store.Get(id);
            if (chat is null)
            {
                return Results.NotFound("Chat not found.");
            }

            return Results.Ok(chat.Messages.Select(m => new ChatMessageDto(m.Role.ToString(), m.Text, m.ToolName)).ToList());
        });
        chats.MapPost("/", async (AgentTurnService turns, SendMessageRequest req, CancellationToken ct) =>
        {
            try
            {
                var turn = await turns.SendAsync(req.Workspace, req.ChatId, req.Message, ct);
                return Results.Ok(turn);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Agent chat rejected");
                return Results.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Agent chat turn failed");
                return Results.Problem(statusCode: 500, title: "Agent chat turn failed", detail: ex.Message);
            }
        });
        chats.MapPost("/{id}/messages", async (AgentTurnService turns, string id, SendMessageRequest req, CancellationToken ct) =>
        {
            try
            {
                var turn = await turns.SendAsync(req.Workspace, id, req.Message, ct);
                return Results.Ok(turn);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Agent chat rejected");
                return Results.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Agent chat turn failed");
                return Results.Problem(statusCode: 500, title: "Agent chat turn failed", detail: ex.Message);
            }
        });
        chats.MapPost("/stream", async (HttpContext ctx, AgentTurnService turns, SendMessageRequest req, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";

            async Task Write(TurnStreamEvent ev)
            {
                var (kind, payload) = ev switch
                {
                    TurnStarted started => ("started", (object)new { chatId = started.ChatId }),
                    TurnTextDelta delta => ("delta", (object)new { delta = delta.Delta }),
                    TurnToolCall tool => ("tool", (object)new { name = tool.Call.Name, arguments = tool.Call.Arguments }),
                    TurnDone done => ("done", (object)done.Response),
                    TurnFailed failed => ("failed", (object)new { message = failed.Message }),
                    _ => ("message", (object)new { })
                };

                if (ev is TurnStarted || ev is TurnDone || ev is TurnFailed)
                {
                    logger.LogInformation("Stream emit {Kind}", kind);
                }

                var json = JsonSerializer.Serialize(payload, StreamJsonOptions);
                await ctx.Response.WriteAsync($"event: {kind}\ndata: {json}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }

            try
            {
                logger.LogInformation("Stream turn begin workspace={Workspace}", req.Workspace);
                await turns.RunTurnAsync(req.Workspace, req.ChatId, req.Message, ct, Write);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Agent chat stream rejected");
                await Write(new TurnFailed(ex.Message));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Agent chat stream failed");
                await Write(new TurnFailed(ex.Message));
            }
        });

        return app;
    }
}
