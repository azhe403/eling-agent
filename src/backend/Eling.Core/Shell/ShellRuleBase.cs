namespace Eling.Core.Shell;

/// <summary>
/// Fields shared by every shell rule. Rules are kept in the configuration
/// file even when they are switched off, so a rule can be re-enabled without
/// losing its history.
/// </summary>
public abstract record ShellRuleBase
{
    /// <summary>
    /// Creates a rule with the defaults the configuration file relies on: a
    /// rule takes effect unless it is switched off, and it carries the time it
    /// was added.
    /// </summary>
    /// <param name="enabled">Whether the rule takes effect.</param>
    /// <param name="createdAt">
    /// When the rule was added. <see cref="default"/> means "not stated", which
    /// is stamped with the current time.
    /// </param>
    /// <param name="updatedAt">When the rule was last changed, if it ever was.</param>
    protected ShellRuleBase(
        bool enabled = true,
        DateTimeOffset createdAt = default,
        DateTimeOffset? updatedAt = null)
    {
        Enabled = enabled;
        CreatedAt = createdAt == default ? DateTimeOffset.UtcNow : createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>Whether the rule takes effect. A disabled rule is kept but ignored.</summary>
    public bool Enabled { get; init; }

    /// <summary>When the rule was added.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the rule was last changed, or null when it never changed.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}
