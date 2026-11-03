using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Filters;

/// <summary>
/// A non-blocking advisory filter that gently reminds an agent that some of
/// what it just did may be worth saving with <c>memory_save</c>.
/// </summary>
/// <remarks>
/// <para>
/// This filter never rejects a call and never changes a tool's own output: it
/// only appends one short paragraph to a successful <c>tools/call</c> result
/// at moments where reusable knowledge plausibly exists, and stays silent
/// everywhere else. The reminder is advice, not enforcement — the agent
/// remains free to decide there is nothing worth saving.
/// </para>
/// <para>
/// The heuristic is deliberately conservative, because a reminder that fires
/// on every call trains the agent to ignore it:
/// <list type="bullet">
/// <item>Only successful calls count. A failed call did not produce verified
/// knowledge, and nudging after an error reads as nagging.</item>
/// <item>Only the tools listed in <see cref="ProductiveTools"/> count: the
/// ones that change the workspace or run something. Read-only exploration
/// and Q&amp;A sessions stay quiet.</item>
/// <item>Once the agent saves anything, the reminder stops for the rest of
/// the session. Quality of the saved memory is not judged here.</item>
/// <item>The reminder fires at a cadence, not on every productive call, so a
/// long build-fix-build loop is nudged a few times rather than dozens.</item>
/// </list>
/// </para>
/// <para>
/// The counts live in a process-scoped singleton keyed on nothing — one MCP
/// stdio process serves exactly one client session, so process lifetime is
/// the session boundary. State is never persisted; a restart resets it.
/// </para>
/// </remarks>
public static class HarvestNudgeFilter
{
    /// <summary>
    /// Tools whose successful use means the agent did real work in the
    /// workspace, which is when reusable knowledge tends to appear.
    /// </summary>
    private static readonly HashSet<string> ProductiveTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_write",
        "file_edit",
        "codebase_index",
        "db_query",
    };

    /// <summary>The tool whose use means the agent already harvested this session.</summary>
    private const string SaveTool = "memory_save";

    /// <summary>
    /// Productive calls required before the first reminder. Deliberately low:
    /// a single edit can already produce a quirk worth saving, and a missed
    /// reminder costs far more than a few extra lines of text.
    /// </summary>
    private const int FirstNudgeAt = 2;

    /// <summary>
    /// Productive calls between later reminders while nothing is saved. Low on
    /// purpose — in a short session the reminder must still land.
    /// </summary>
    private const int RepeatEvery = 3;

    /// <summary>Independent of the action, but keeps the text short enough to be read.</summary>
    private const string ReminderText =
        "\n\n---\n" +
        "eling reminder (advisory, not a requirement): you have made several changes in this workspace " +
        "and have not saved any memory yet this session. Ask yourself once: is there anything here that " +
        "a future session or a different agent would otherwise have to rediscover? If so — a command that " +
        "works and its exact invocation, an environment or toolchain quirk and its workaround, a decision " +
        "and the reason behind it, a dead end already ruled out, a convention and the constraint that forces " +
        "it — save each one with `memory_save` as a standalone fact. If nothing here is reusable, ignore this " +
        "and carry on; that is a perfectly good answer.";

    /// <summary>Creates the tools/call filter that appends the harvest reminder.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create()
    {
        var state = new NudgeState();

        return next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);

            var toolName = context.Params?.Name;
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return result;
            }

            if (string.Equals(toolName, SaveTool, StringComparison.OrdinalIgnoreCase))
            {
                state.RecordSave();
                return result;
            }

            // Failures teach nothing verified, and nudging after one reads as nagging.
            if (result.IsError == true || !ProductiveTools.Contains(toolName))
            {
                return result;
            }

            return state.ShouldNudge() ? AppendReminder(result) : result;
        };
    }

    private static CallToolResult AppendReminder(CallToolResult result)
    {
        result.Content ??= [];

        // Copy the existing blocks rather than mutating a list the tool may
        // still be holding a reference to. The reminder is the new last block.
        var content = new List<ContentBlock>(result.Content.Count + 1);
        content.AddRange(result.Content);
        content.Add(new TextContentBlock { Text = ReminderText });
        result.Content = content;

        return result;
    }

    /// <summary>
    /// Tracks per-process progress toward a reminder. Guarded by a private
    /// gate: MCP filters are invoked concurrently for pipelined requests.
    /// </summary>
    private sealed class NudgeState
    {
        private readonly object _gate = new();
        private int _productiveCalls;
        private int _lastNudgedAt = -1;
        private bool _savedAnything;

        /// <summary>Set once the agent saves anything; silences the reminder for good.</summary>
        public void RecordSave()
        {
            lock (_gate)
            {
                _savedAnything = true;
            }
        }

        /// <summary>
        /// Records one productive call and reports whether this call should
        /// carry the reminder.
        /// </summary>
        public bool ShouldNudge()
        {
            lock (_gate)
            {
                _productiveCalls++;

                if (_savedAnything)
                {
                    return false;
                }

                if (_productiveCalls < FirstNudgeAt)
                {
                    return false;
                }

                if (_lastNudgedAt >= 0 && _productiveCalls - _lastNudgedAt < RepeatEvery)
                {
                    return false;
                }

                _lastNudgedAt = _productiveCalls;
                return true;
            }
        }
    }
}
