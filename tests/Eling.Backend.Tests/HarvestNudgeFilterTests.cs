using System.Reflection;
using System.Runtime.CompilerServices;
using Eling.Backend.Mcp.Filters;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// Covers <see cref="HarvestNudgeFilter"/>: the reminder is advisory, fires
/// only after real work with nothing saved yet, and never alters a tool's
/// own result beyond appending one extra block.
/// </summary>
public sealed class HarvestNudgeFilterTests
{
    private const string ReminderMarker = "eling reminder";

    private static RequestContext<CallToolRequestParams> CreateRequestContext(string toolName)
    {
        var requestParams = new CallToolRequestParams { Name = toolName };
        var context = (RequestContext<CallToolRequestParams>)RuntimeHelpers
            .GetUninitializedObject(typeof(RequestContext<CallToolRequestParams>));
        var field = typeof(RequestContext<CallToolRequestParams>)
            .GetField("<Params>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(context, requestParams);
        return context;
    }

    private static CallToolResult OkResult(string text = "tool output") => new()
    {
        IsError = false,
        Content = [new TextContentBlock { Text = text }]
    };

    /// <summary>
    /// Runs one call through the filter, returning what the agent would see.
    /// </summary>
    private static async Task<CallToolResult> CallAsync(
        McpRequestFilter<CallToolRequestParams, CallToolResult> filter,
        string toolName,
        CallToolResult? result = null)
    {
        var reply = result ?? OkResult();
        McpRequestHandler<CallToolRequestParams, CallToolResult> next =
            (_, _) => new ValueTask<CallToolResult>(reply);

        return await filter(next)(CreateRequestContext(toolName), CancellationToken.None);
    }

    private static bool HasReminder(CallToolResult result) =>
        result.Content?.Any(block =>
            block is TextContentBlock t &&
            t.Text.Contains(ReminderMarker, StringComparison.Ordinal)) == true;

    [Fact]
    public async Task ReadOnlyCalls_NeverNudged()
    {
        var filter = HarvestNudgeFilter.Create();

        for (var i = 0; i < 10; i++)
        {
            Assert.False(HasReminder(await CallAsync(filter, "recall")));
        }
    }

    [Fact]
    public async Task SingleProductiveCall_NotNudgedYet()
    {
        var filter = HarvestNudgeFilter.Create();

        Assert.False(HasReminder(await CallAsync(filter, "file_edit")));
    }

    [Fact]
    public async Task ProductiveWorkWithoutSave_Nudges()
    {
        var filter = HarvestNudgeFilter.Create();

        await CallAsync(filter, "file_edit");

        Assert.True(HasReminder(await CallAsync(filter, "file_write")));
    }

    [Fact]
    public async Task DbQuery_IsProductiveTool_Nudges()
    {
        var filter = HarvestNudgeFilter.Create();

        await CallAsync(filter, "db_query");

        Assert.True(HasReminder(await CallAsync(filter, "db_query")));
    }

    [Fact]
    public async Task FailedCall_DoesNotNudge()
    {
        var filter = HarvestNudgeFilter.Create();
        await CallAsync(filter, "file_edit");

        var failure = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "boom" }]
        };

        Assert.False(HasReminder(await CallAsync(filter, "file_write", failure)));
    }

    [Fact]
    public async Task AfterSave_SilencedForRestOfSession()
    {
        var filter = HarvestNudgeFilter.Create();

        await CallAsync(filter, "memory_save");

        for (var i = 0; i < 10; i++)
        {
            Assert.False(HasReminder(await CallAsync(filter, "file_write")));
        }
    }

    [Fact]
    public async Task Nudge_IsNeverAnError()
    {
        var filter = HarvestNudgeFilter.Create();
        await CallAsync(filter, "file_edit");

        var result = await CallAsync(filter, "file_write");

        // Advisory means advisory: the call must still read as successful.
        Assert.NotEqual(true, result.IsError);
    }

    [Fact]
    public async Task Reminder_AppendsWithoutDroppingToolOutput()
    {
        var filter = HarvestNudgeFilter.Create();
        await CallAsync(filter, "file_edit");

        var result = await CallAsync(filter, "file_edit", OkResult("the real output"));

        Assert.Equal("the real output", Assert.IsType<TextContentBlock>(result.Content![0]).Text);
        Assert.True(HasReminder(result));
    }
}
