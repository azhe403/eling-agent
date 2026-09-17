namespace Eling.Backend.Dtos;

public record ChatSummary(string Id, string Workspace, string Title, DateTimeOffset UpdatedAt);

public record ChatMessageDto(string Role, string Text, string? ToolName);

public record SendMessageRequest(string Workspace, string Message, string? ChatId);

public record ToolCallDto(string Name, string Arguments);

public record TurnResponse(string ChatId, string Assistant, List<ToolCallDto> ToolCalls);
