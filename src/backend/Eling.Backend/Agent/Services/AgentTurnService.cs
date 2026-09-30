using Eling.Backend.Agent.Ports;
using Eling.Backend.Dtos;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Agent.Services;

public sealed class AgentTurnService(
    IChatGateway gateway,
    IEnumerable<IAgentTool> tools,
    BackendChatStore chats,
    ProviderStore provider,
    ILogger<AgentTurnService> logger)
{
    private const int HistoryCap = 40;

    private const string SystemPromptTemplate =
        "You are Eling, an intelligent coding and persistent memory assistant.\n" +
        "Your current working directory (CWD) is: {0}\n" +
        "You must strictly operate within this active workspace directory: {0}\n" +
        "You have direct access to Eling's official MCP tools: memory_recall, memory_save, file_read, file_write, directory_list, glob, file_search, etc. " +
        "Always prefer recalling relevant memories with memory_recall and checking files before answering. " +
        "Mandatory session-start behavior: in the first user turn of every new session, call memory_recall before any other tool call or reply, even when the message looks casual or ambiguous. " +
        "If a tool reports an error, state it honestly.";

    public Task<TurnResponse> SendAsync(string workspace, string? chatId, string message, CancellationToken ct) =>
        RunTurnAsync(workspace, chatId, message, ct, onEvent: null);

    public async Task<TurnResponse> RunTurnAsync(
        string workspace,
        string? chatId,
        string message,
        CancellationToken ct,
        Func<TurnStreamEvent, Task>? onEvent)
    {
        Task Emit(TurnStreamEvent ev) => onEvent?.Invoke(ev) ?? Task.CompletedTask;

        var chat = chats.GetOrCreate(workspace, chatId);
        chats.Append(chat, new AgentMessage(AgentRole.User, message));
        await Emit(new TurnStarted(chat.Id));

        var toolList = tools.ToList();
        var systemPrompt = string.Format(SystemPromptTemplate, workspace);
        var executedCalls = new List<ToolCallDto>();
        string finalText = string.Empty;
        const int MaxHops = 25;

        while (!ct.IsCancellationRequested)
        {
            if (executedCalls.Count >= MaxHops)
            {
                var summary = executedCalls.Count == 0 ? "(none)" : string.Join(", ", executedCalls.Select(c => c.Name));
                var lastAssistant = chat.Messages.LastOrDefault(m => m.Role == AgentRole.Assistant)?.Text;
                finalText = string.IsNullOrWhiteSpace(lastAssistant)
                    ? $"[done] Safety limit {MaxHops} hops reached — executed {executedCalls.Count} tool(s): {summary}."
                    : $"[done] Safety limit {MaxHops} hops reached — executed {executedCalls.Count} tool(s): {summary}. Last response: {lastAssistant}";
                break;
            }

            var history = chat.Messages.TakeLast(HistoryCap).ToList();
            var request = new SingleShotRequest(
                ResolveModel(),
                [new AgentMessage(AgentRole.System, systemPrompt), .. history],
                toolList.Select(t => new ToolDefinition(t.Name, t.Description, t.ParametersJsonSchema)).ToList());

            SingleShotResult turn;
            try
            {
                if (onEvent is null)
                {
                    turn = await gateway.CompleteAsync(request, ct);
                }
                else
                {
                    turn = await CompleteStreamingWithFallbackAsync(request, ct, Emit);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agent turn gateway call failed");
                throw;
            }

            logger.LogInformation(
                "Agent hop finished: toolCalls={ToolCount}, finishReason={FinishReason}, textLen={TextLen}",
                turn.ToolCalls.Count,
                turn.FinishReason ?? "(null)",
                turn.AssistantText?.Length ?? 0);

            chats.Append(chat, new AgentMessage(AgentRole.Assistant, turn.AssistantText ?? string.Empty));

            if (turn.ToolCalls.Count == 0)
            {
                // If provider truncated output due to max_tokens, force continuation
                if (string.Equals(turn.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Provider stopped due to length limit. Requesting continuation.");
                    chats.Append(chat, new AgentMessage(AgentRole.User, "Continue exactly where you left off without repeating previous text."));
                    continue; 
                }

                finalText = turn.AssistantText ?? string.Empty;
                break;
            }

            if (executedCalls.Count + turn.ToolCalls.Count > MaxHops)
            {
                var summary = executedCalls.Count == 0 ? "(none)" : string.Join(", ", executedCalls.Select(c => c.Name));
                var lastAssistant = chat.Messages.LastOrDefault(m => m.Role == AgentRole.Assistant)?.Text;
                finalText = string.IsNullOrWhiteSpace(lastAssistant)
                    ? $"[done] Safety limit {MaxHops} hops reached — pending {turn.ToolCalls.Count} tool(s) skipped. Executed {executedCalls.Count}: {summary}."
                    : $"[done] Safety limit {MaxHops} hops reached — pending {turn.ToolCalls.Count} tool(s) skipped. Executed {executedCalls.Count}: {summary}. Last response: {lastAssistant}";
                break;
            }

            foreach (var call in turn.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                var dto = new ToolCallDto(call.Name, call.ArgumentsJson);
                await Emit(new TurnToolCall(dto));
                var tool = toolList.FirstOrDefault(t => string.Equals(t.Name, call.Name, StringComparison.OrdinalIgnoreCase));
                string output;
                if (tool is null)
                {
                    logger.LogWarning("Agent tool not available: '{Name}'. Registered: {Registered}", call.Name, string.Join(",", toolList.Select(t => t.Name)));
                    output = $"error: tool not available: {call.Name}";
                }
                else
                {
                    try
                    {
                        output = await tool.ExecuteAsync(call.ArgumentsJson, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Agent tool {Name} failed", call.Name);
                        output = $"error: {ex.Message}";
                    }
                }

                executedCalls.Add(dto);
                logger.LogInformation("Agent tool executed: name={Name}, argsLen={ArgsLen}, outputLen={OutputLen}", call.Name, call.ArgumentsJson?.Length ?? 0, output?.Length ?? 0);
                await Emit(new TurnToolResult(call.Name, output ?? string.Empty));
                chats.Append(chat, new AgentMessage(AgentRole.Tool, output, call.Id, call.Name));
            }

        }

        var preview = finalText.Length > 200 ? finalText[..200] + "..." : finalText;
        logger.LogInformation(
            "Agent turn finished: executed={Executed}, finalLen={FinalLen}, preview={Preview}",
            executedCalls.Count,
            finalText.Length,
            preview);

        var response = new TurnResponse(chat.Id, finalText, executedCalls);
        await Emit(new TurnDone(response));
        return response;
    }

    private async Task<SingleShotResult> CompleteStreamingWithFallbackAsync(
        SingleShotRequest request,
        CancellationToken ct,
        Func<TurnStreamEvent, Task> emit)
    {
        var text = new System.Text.StringBuilder();
        var calls = new List<ToolCallRequest>();
        string? finishReason = null;
        try
        {
            await foreach (var ev in gateway.CompleteStreamingAsync(request, ct))
            {
                switch (ev)
                {
                    case ChatTextDelta delta:
                        text.Append(delta.Delta);
                        await emit(new TurnTextDelta(delta.Delta));
                        break;
                    case ChatToolRequest req:
                        calls.Add(req.Call);
                        break;
                    case ChatFinishReasonEvent fr:
                        finishReason = fr.Reason;
                        break;
                }
            }

            return new SingleShotResult(text.Length > 0 ? text.ToString() : null, calls, finishReason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Streaming failed, falling back to single-shot completion");
            using var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            fallbackCts.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                var fallback = await gateway.CompleteAsync(request, fallbackCts.Token);
                if (!string.IsNullOrEmpty(fallback.AssistantText))
                {
                    await emit(new TurnTextDelta(fallback.AssistantText));
                }

                return fallback;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException("Provider chat request timed out.");
            }
        }
    }

    private string ResolveModel()
    {
        return provider.TryGetConfig(out _, out var model, out _) && !string.IsNullOrWhiteSpace(model)
            ? model
            : "default";
    }
}
