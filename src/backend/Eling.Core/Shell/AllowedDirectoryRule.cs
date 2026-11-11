using System.Text.Json.Serialization;

namespace Eling.Core.Shell;

/// <summary>
/// A directory outside the working directory that commands are allowed to run
/// in while the sandbox restricts execution to the working directory.
/// </summary>
public sealed record AllowedDirectoryRule : ShellRuleBase
{
    /// <summary>
    /// Creates a rule. The constructor is the deserialization constructor
    /// because the source-generated JSON metadata assigns every property it
    /// knows about, which would otherwise replace the defaults with
    /// <c>default</c> whenever the file leaves a field out.
    /// </summary>
    /// <param name="path">Path of the directory the rule allows.</param>
    /// <param name="enabled">Whether the rule takes effect.</param>
    /// <param name="createdAt">
    /// When the rule was added. <see cref="default"/> means "not stated", which
    /// is stamped with the current time.
    /// </param>
    /// <param name="updatedAt">When the rule was last changed, if it ever was.</param>
    [JsonConstructor]
    public AllowedDirectoryRule(
        string path = "",
        bool enabled = true,
        DateTimeOffset createdAt = default,
        DateTimeOffset? updatedAt = null)
        : base(enabled, createdAt, updatedAt)
    {
        Path = path;
    }

    /// <summary>Path of the directory the rule allows. Optional at the JSON boundary for the same reason as <see cref="BlockedCommandRule.Pattern"/> — the store drops empty paths.</summary>
    public string Path { get; init; }
}
