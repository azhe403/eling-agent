namespace Eling.Desktop.Models;

public record ChatSummaryDto(string Id, string Workspace, string Title, System.DateTimeOffset UpdatedAt);

public record ChatMessageDto(string Role, string Text, string? ToolName);

public record TurnDto(string ChatId, string Assistant, List<ToolCallDto> ToolCalls);

public record ToolCallDto(string Name, string Arguments);
