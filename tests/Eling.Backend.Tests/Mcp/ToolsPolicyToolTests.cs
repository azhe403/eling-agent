using System.Text.Json;
using Eling.Backend.Mcp.Tools;
using Eling.Backend.Tools;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests.Mcp;

/// <summary>
/// Verifies the conversational tools_policy tool: list reports live state,
/// enable/disable/reset mutate the store, groups toggle every member, protected
/// tools cannot be disabled, and unknown actions/tools/groups are rejected
/// without mutating state.
/// </summary>
public sealed class ToolsPolicyToolTests : IDisposable
{
    private readonly ToolPolicyStore _store;
    private readonly ToolsPolicyTool _tool;
    private readonly string _root;

    public ToolsPolicyToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"eling-tools-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new ToolPolicyStore(new UserScope(_root), NullLogger<ToolPolicyStore>.Instance);
        _tool = new ToolsPolicyTool(_store, null);
    }

    [Fact]
    public async Task List_ReportsAllToolsWithProtectedFlags()
    {
        var result = await _tool.RunPolicyAsync("list");

        using var document = JsonDocument.Parse(result);
        Assert.False(TryGetError(document, out _), "list must not return an error");
        Assert.True(IsEnabled(document, "file_search"));
        Assert.True(IsProtected(document, "tools_policy"));
        Assert.True(IsProtected(document, "memory_recall"));
        Assert.False(IsProtected(document, "file_search"));
    }

    [Fact]
    public async Task Disable_MarksToolDisabledInStoreAndResponse()
    {
        var result = await _tool.RunPolicyAsync("disable", tools: ["file_search"]);

        Assert.True(_store.IsToolDisabled("file_search"));
        using var document = JsonDocument.Parse(result);
        Assert.False(IsEnabled(document, "file_search"));
        Assert.True(IsEnabled(document, "file_read"));
    }

    [Fact]
    public async Task Disable_ProtectedTool_IsRejectedWithoutMutatingState()
    {
        var result = await _tool.RunPolicyAsync("disable", tools: ["tools_policy"]);

        Assert.False(_store.IsToolDisabled("tools_policy"));
        using var document = JsonDocument.Parse(result);
        Assert.True(TryGetError(document, out var error));
        Assert.Contains("tools_policy", error);
    }

    [Fact]
    public async Task Disable_UnknownTool_IsRejectedWithoutMutatingState()
    {
        var result = await _tool.RunPolicyAsync("disable", tools: ["not_a_tool"]);

        Assert.Empty(_store.GetDisabledTools());
        using var document = JsonDocument.Parse(result);
        Assert.True(TryGetError(document, out var error));
        Assert.Contains("not_a_tool", error);
    }

    [Fact]
    public async Task Disable_Group_DisablesEveryMemberOfTheGroup()
    {
        await _tool.RunPolicyAsync("disable", group: "filesystem");

        Assert.True(_store.IsToolDisabled("file_search"));
        Assert.True(_store.IsToolDisabled("file_delete"));
        Assert.False(_store.IsToolDisabled("memory_recall"));
        Assert.False(_store.IsToolDisabled("codebase_search"));
    }

    [Fact]
    public async Task Disable_UnknownGroup_IsRejectedWithoutMutatingState()
    {
        var result = await _tool.RunPolicyAsync("disable", group: "hardware");

        Assert.Empty(_store.GetDisabledTools());
        using var document = JsonDocument.Parse(result);
        Assert.True(TryGetError(document, out var error));
        Assert.Contains("hardware", error);
    }

    [Fact]
    public async Task Enable_AfterDisable_RestoresTool()
    {
        await _tool.RunPolicyAsync("disable", tools: ["file_search"]);

        var result = await _tool.RunPolicyAsync("enable", tools: ["file_search"]);

        Assert.False(_store.IsToolDisabled("file_search"));
        using var document = JsonDocument.Parse(result);
        Assert.True(IsEnabled(document, "file_search"));
    }

    [Fact]
    public async Task Reset_ClearsEveryDisabledTool()
    {
        await _tool.RunPolicyAsync("disable", group: "filesystem");
        await _tool.RunPolicyAsync("disable", group: "memory");

        var result = await _tool.RunPolicyAsync("reset");

        Assert.Empty(_store.GetDisabledTools());
        using var document = JsonDocument.Parse(result);
        Assert.True(IsEnabled(document, "file_search"));
        Assert.True(IsEnabled(document, "memory_save"));
    }

    [Fact]
    public async Task UnknownAction_ReturnsErrorWithoutMutatingState()
    {
        var result = await _tool.RunPolicyAsync("explode");

        Assert.Empty(_store.GetDisabledTools());
        using var document = JsonDocument.Parse(result);
        Assert.True(TryGetError(document, out var error));
        Assert.Contains("explode", error);
    }

    [Fact]
    public async Task Disable_WithoutToolsOrGroup_ReturnsErrorWithoutMutatingState()
    {
        var result = await _tool.RunPolicyAsync("disable");

        Assert.Empty(_store.GetDisabledTools());
        using var document = JsonDocument.Parse(result);
        Assert.True(TryGetError(document, out _));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static bool TryGetError(JsonDocument document, out string error)
    {
        if (document.RootElement.TryGetProperty("error", out var errorElement))
        {
            error = errorElement.GetString() ?? string.Empty;
            return true;
        }

        error = string.Empty;
        return false;
    }

    private static bool IsEnabled(JsonDocument document, string toolName)
        => FindTool(document, toolName).GetProperty("enabled").GetBoolean();

    private static bool IsProtected(JsonDocument document, string toolName)
        => FindTool(document, toolName).GetProperty("isProtected").GetBoolean();

    private static JsonElement FindTool(JsonDocument document, string toolName)
    {
        foreach (var tool in document.RootElement.GetProperty("tools").EnumerateArray())
        {
            if (tool.GetProperty("name").GetString() == toolName)
            {
                return tool;
            }
        }

        throw new InvalidOperationException($"Tool '{toolName}' not found in tools_policy response.");
    }
}
