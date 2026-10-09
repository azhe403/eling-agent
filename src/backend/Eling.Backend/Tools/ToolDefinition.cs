namespace Eling.Backend.Tools;

/// <summary>Catalog entry describing one MCP tool for dashboard display.</summary>
public sealed record ToolDefinition(
    string Name,
    string Group,
    string Description);
