namespace Eling.Backend.Agent.Ports;

public enum AgentRole
{
    System,
    User,
    Assistant,
    Tool
}

public record AgentMessage(AgentRole Role, string Text, string? ToolCallId = null, string? ToolName = null);

public record ToolDefinition(string Name, string Description, string ParametersJsonSchema);

public record ToolCallRequest(string Id, string Name, string ArgumentsJson);

public record SingleShotRequest(string Model, IReadOnlyList<AgentMessage> Messages, IReadOnlyList<ToolDefinition> Tools);

public record SingleShotResult(string? AssistantText, IReadOnlyList<ToolCallRequest> ToolCalls);

public abstract record ChatStreamEvent;

public record ChatTextDelta(string Delta) : ChatStreamEvent;

public record ChatToolRequest(ToolCallRequest Call) : ChatStreamEvent;
