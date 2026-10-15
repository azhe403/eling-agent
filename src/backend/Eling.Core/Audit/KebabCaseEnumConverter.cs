using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eling.Core.Audit;

/// <summary>
/// String-enum converter that writes and reads members in kebab-case, matching the
/// lowercase enum values the audit trail stores on disk ("change", "memory", "project",
/// "project-local").
/// </summary>
/// <remarks>
/// <see cref="JsonSourceGenerationOptionsAttribute.UseStringEnumConverter"/> has no naming
/// policy to carry, so it spells members as declared ("Change", "Memory", "Project"); this
/// converter is registered per enum in <see cref="AuditJsonContext"/> instead. It is generic
/// so one class covers <see cref="AuditTrail"/>, <see cref="AuditCategory"/>,
/// <see cref="AuditOutcome"/> and <see cref="Eling.Core.Memory.MemoryScopeKind"/>, which cannot
/// carry an attribute of its own because the audit trail does not own that type.
/// <para>
/// Kebab-case rather than camelCase because the scope labels are canonical in this repo:
/// <c>MemoryScopeLabels</c> maps <c>MemoryScopeKind.ProjectLocal</c> to "project-local", and
/// <c>MemoryScopeParser</c> accepts only "project", "project-local" and "global". A single-word
/// member is unchanged by the policy, so every other enum value still serializes as the
/// lowercase single word the design specifies. Note the policy's name is
/// <see cref="JsonNamingPolicy.KebabCaseLower"/> — there is no <c>KebabCase</c> member.
/// </para>
/// </remarks>
internal sealed class KebabCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public KebabCaseEnumConverter()
        : base(JsonNamingPolicy.KebabCaseLower)
    {
    }
}
