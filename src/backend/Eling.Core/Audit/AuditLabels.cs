using Eling.Core.Memory;

namespace Eling.Core.Audit;

/// <summary>
/// Single home for the canonical on-disk spelling of every audit label
/// (amendment C1). The JSONL is the reference vocabulary — "change", "memory",
/// "success", "project-local" — and the rollup record reaches the same strings
/// through the kebab-case converters on <see cref="AuditJsonContext"/>. The
/// SQLite index used to spell a third vocabulary of its own
/// ("Change"/"Memory"/"ProjectLocal"), in the one artifact that claims to
/// mirror the trail; its write sites now route through here.
/// </summary>
/// <remarks>
/// Kebab-case rather than the declared enum names because the scope labels are
/// canonical repo-wide: <see cref="MemoryScopeLabels"/> maps
/// <see cref="MemoryScopeKind.ProjectLocal"/> to "project-local", so the audit
/// labels delegate to it instead of spelling the scope vocabulary a second
/// time. An unrecognised member throws rather than falling back to its declared
/// name — a silently PascalCase label is exactly the drift this helper exists
/// to prevent, so a new domain (<c>codebase</c>, <c>tools</c>) has to land here
/// on purpose.
/// </remarks>
internal static class AuditLabels
{
    public static string ToWireString(AuditTrail trail) => trail switch
    {
        AuditTrail.Change => "change",
        AuditTrail.Access => "access",
        _ => throw new ArgumentOutOfRangeException(nameof(trail), trail, "Unknown audit trail.")
    };

    public static string ToWireString(AuditCategory category) => category switch
    {
        AuditCategory.Memory => "memory",
        AuditCategory.Runtime => "runtime",
        AuditCategory.Filesystem => "filesystem",
        AuditCategory.Codebase => "codebase",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown audit category.")
    };

    public static string ToWireString(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Success => "success",
        AuditOutcome.Error => "error",
        AuditOutcome.Denied => "denied",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown audit outcome.")
    };

    public static string ToWireString(MemoryScopeKind scope) => MemoryScopeLabels.ToWireString(scope);
}
