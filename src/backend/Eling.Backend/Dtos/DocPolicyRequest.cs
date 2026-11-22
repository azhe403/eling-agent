using Eling.Core.Docs;

namespace Eling.Backend.Dtos;

/// <summary>
/// Request to set the docs policy.
/// </summary>
public sealed record DocPolicyRequest(
    CoveragePolicyDto? Coverage = null,
    string? Decision = null); // ask, disabled, clear