using System.Text.Json.Serialization;

namespace Eling.Core.Shell;

/// <summary>
/// A command fragment that must never run. The fragment is matched against the
/// whole command line, so it can be as short as a single dangerous word.
/// </summary>
public sealed record BlockedCommandRule : ShellRuleBase
{
    /// <summary>
    /// Creates a rule. The constructor is the deserialization constructor
    /// because the source-generated JSON metadata assigns every property it
    /// knows about, which would otherwise replace the defaults with
    /// <c>default</c> whenever the file leaves a field out.
    /// </summary>
    /// <param name="pattern">Fragment matched against the command line.</param>
    /// <param name="enabled">Whether the rule takes effect.</param>
    /// <param name="createdAt">
    /// When the rule was added. <see cref="default"/> means "not stated", which
    /// is stamped with the current time.
    /// </param>
    /// <param name="updatedAt">When the rule was last changed, if it ever was.</param>
    [JsonConstructor]
    public BlockedCommandRule(
        string pattern = "",
        bool enabled = true,
        DateTimeOffset createdAt = default,
        DateTimeOffset? updatedAt = null)
        : base(enabled, createdAt, updatedAt)
    {
        Pattern = pattern;
    }

    /// <summary>Fragment matched against the command line. Kept optional at the JSON boundary so a rule written as `{}` deserializes to an inert empty rule; the store drops those (`ReadConfig_IgnoresEmptyPatterns`) rather than let them block everything.</summary>
    public string Pattern { get; init; }
}
