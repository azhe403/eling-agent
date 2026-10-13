using System.ComponentModel;
using System.Text.Json;
using Eling.Backend.Dtos;
using Eling.Backend.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Chat-facing control for the per-user tool policy: inspect the live
/// enabled/disabled state of every MCP tool and flip individual tools or whole
/// groups. Changes persist through <see cref="ToolPolicyStore"/> and are
/// enforced by the MCP request filters on the next request, without a restart.
/// </summary>
[McpServerToolType]
public sealed class ToolsPolicyTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ToolPolicyStore _store;
    private readonly ILogger<ToolsPolicyTool>? _logger;

    public ToolsPolicyTool(ToolPolicyStore store, ILogger<ToolsPolicyTool>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Inspects or modifies the enabled state of MCP tools in real time.
    /// </summary>
    [McpServerTool(Name = "tools_policy"), Description("Inspect or modify the enabled state of MCP tools in real time. Actions: 'list', 'enable', 'disable', 'reset'. Changes take effect immediately without restarting.")]
    public Task<string> RunPolicyAsync(
        [Description("One of: 'list', 'enable', 'disable', 'reset'.")] string action,
        [Description("Tool names to target, e.g. ['file_delete', 'file_edit']. Mutually exclusive with 'group'.")] string[]? tools = null,
        [Description("Tool group to target: 'memory', 'codebase', or 'filesystem'. Mutually exclusive with 'tools'.")] string? group = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = action?.Trim().ToLowerInvariant() ?? string.Empty;
        string? error = null;

        switch (normalized)
        {
            case "list":
                break;

            case "enable":
                error = ApplyTargets(true, tools, group);
                break;

            case "disable":
                error = ApplyTargets(false, tools, group);
                break;

            case "reset":
                _store.Reset();
                _logger?.LogInformation("tools_policy: reset all tool enablement state");
                break;

            default:
                return Task.FromResult(SerializeError(
                    $"Unknown action '{action}'. Use 'list', 'enable', 'disable', or 'reset'."));
        }

        if (error is not null)
        {
            return Task.FromResult(SerializeError(error));
        }

        return Task.FromResult(SerializeState(normalized));
    }

    /// <summary>
    /// Applies enable/disable to the requested tools or group, returning an
    /// error message when the target is invalid, empty, or protected.
    /// </summary>
    private string? ApplyTargets(bool enable, string[]? tools, string? group)
    {
        if (tools is { Length: > 0 })
        {
            var requested = tools.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).ToList();
            var unknown = requested.Where(name => !ToolCatalog.Exists(name)).ToList();
            if (unknown.Count > 0)
            {
                return $"Unknown tool(s): {string.Join(", ", unknown)}.";
            }

            if (!enable)
            {
                var protectedTools = requested.Where(ToolPolicyStore.IsProtected).ToList();
                if (protectedTools.Count > 0)
                {
                    return $"Tool(s) {string.Join(", ", protectedTools)} are protected and cannot be disabled.";
                }
            }

            if (enable)
            {
                _store.EnableTools(requested);
            }
            else
            {
                _store.DisableTools(requested);
            }

            _logger?.LogInformation(
                "tools_policy: {Action} tools {Tools}",
                enable ? "enable" : "disable",
                string.Join(", ", requested));
            return null;
        }

        if (!string.IsNullOrWhiteSpace(group))
        {
            var trimmedGroup = group.Trim();
            var members = ToolCatalog.MembersOf(trimmedGroup);
            if (members.Count == 0)
            {
                return $"Unknown tool group '{trimmedGroup}'. Use 'memory', 'codebase', or 'filesystem'.";
            }

            if (enable)
            {
                _store.EnableTools(members);
            }
            else
            {
                _store.DisableTools(members);
            }

            _logger?.LogInformation(
                "tools_policy: {Action} group {Group} ({Count} tools)",
                enable ? "enable" : "disable",
                trimmedGroup,
                members.Count);
            return null;
        }

        return "Provide 'tools' (a list of tool names) or 'group' (" +
            $"{ToolCatalog.MemoryGroup}, {ToolCatalog.CodebaseGroup}, or {ToolCatalog.FilesystemGroup}).";
    }

    private List<ToolItemDto> ListTools()
        => ToolCatalog.All
            .Select(tool => new ToolItemDto(
                tool.Name,
                tool.Group,
                tool.Description,
                !_store.IsToolDisabled(tool.Name),
                ToolPolicyStore.IsProtected(tool.Name)))
            .ToList();

    private string SerializeState(string action)
        => JsonSerializer.Serialize(new { action, tools = ListTools() }, JsonOptions);

    private static string SerializeError(string message)
        => JsonSerializer.Serialize(new { error = message }, JsonOptions);
}
