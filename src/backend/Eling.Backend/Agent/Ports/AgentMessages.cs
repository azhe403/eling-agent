namespace Eling.Backend.Agent.Ports;

public enum AgentRole
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>
/// One turn of conversation. <paramref name="ToolCalls"/> is set on an assistant
/// message that called tools and MUST be replayed verbatim with it: a `tool`
/// message is only valid as the answer to a declared tool_call, so dropping these
/// orphans the tool message and providers reject the whole request.
/// </summary>
public record AgentMessage(
    AgentRole Role,
    string Text,
    string? ToolCallId = null,
    string? ToolName = null,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null);

public record ToolDefinition(
    string Name,
    string Description,
    string ParametersJsonSchema);

public record ToolCallRequest(
    string Id,
    string Name,
    string ArgumentsJson);

/// <summary>
/// One provider round-trip. <paramref name="SessionId"/> is the chat's own id,
/// carried through so the gateway can label the request to the provider — it is
/// not a user setting and never comes from configuration.
/// </summary>
public record SingleShotRequest(
    string Model,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    string? SessionId = null);

public record SingleShotResult(
    string? AssistantText,
    IReadOnlyList<ToolCallRequest> ToolCalls,
    string? FinishReason = null);

public abstract record ChatStreamEvent;

public record ChatTextDelta(string Delta) : ChatStreamEvent;

public record ChatToolRequest(ToolCallRequest Call) : ChatStreamEvent;

public record ChatFinishReasonEvent(string Reason) : ChatStreamEvent;
