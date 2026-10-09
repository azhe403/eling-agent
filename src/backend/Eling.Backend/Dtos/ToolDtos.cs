namespace Eling.Backend.Dtos;

/// <summary>Dashboard view of one MCP tool with its live policy state.</summary>
public sealed record ToolItemDto(
    string Name,
    string Group,
    string Description,
    bool Enabled,
    bool IsProtected);

/// <summary>Dashboard request to flip one tool or a whole group.</summary>
public sealed record UpdateToolPolicyRequest(
    string? ToolName,
    string? Group,
    bool Enabled);
