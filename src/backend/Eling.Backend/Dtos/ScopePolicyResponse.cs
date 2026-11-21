using Eling.Core.Docs;

namespace Eling.Backend.Dtos;

/// <summary>
/// Response for doc_scope_policy.
/// </summary>
public sealed record ScopePolicyResponse(
    CoveragePolicyDto Policy,
    string? Decision)
{
    public static ScopePolicyResponse From(CoveragePolicy policy, string? decision = null) =>
        new(new CoveragePolicyDto(policy.Include, policy.Exclude), decision);
}