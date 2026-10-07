using Eling.Backend.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Eling.Backend.Controllers;

/// <summary>
/// The runtime list the dashboard's codebase picker reads.
/// </summary>
/// <remarks>
/// A controller rather than a minimal-API handler because the coordinator group
/// is being migrated off minimal APIs, and new routes should not extend the
/// style being retired. Only <c>GET /runtimes</c> lives here so far; the other
/// coordinator routes remain in <c>Endpoints/CoordinatorEndpoints.cs</c> and are
/// deliberately untouched.
/// <para>
/// The two modes exist because the subdomains need different answers, and
/// forcing one answer on both was the bug:
/// <list type="bullet">
/// <item>
/// default — live processes only. A memory service used to be resolved this way,
/// which hid any scope nobody happened to be running in.
/// </item>
/// <item>
/// <c>includeRegistered</c> — every workspace that has ever registered, including
/// closed ones. Codebase needs this: its indexes are read-only files in a global
/// store and stay readable after the owning process exits.
/// </item>
/// </list>
/// Memory's own picker does not read this endpoint at all; it reads
/// <c>/api/project/scopes</c>, which resolves the <c>.eling</c> chain.
/// </para>
/// </remarks>
[ApiController]
[Route("api/coordinator")]
public sealed class CoordinatorController(RuntimeRegistry registry) : ControllerBase
{
    /// <summary>
    /// Registered runtimes. With <paramref name="includeRegistered"/> the result
    /// also contains workspaces whose process has exited; those carry
    /// <c>processId = 0</c> and <c>isAlive = false</c>, and otherwise have the
    /// same shape as a live entry, so consumers need no special case.
    /// </summary>
    [HttpGet("runtimes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<RuntimeInfoDto>>> GetRuntimesAsync(
        [FromQuery] bool includeRegistered = false,
        CancellationToken cancellationToken = default)
    {
        var runtimes = includeRegistered
            ? await registry.AliveOrRegisteredAsync(cancellationToken)
            : registry.Alive();

        return Ok(runtimes.Select(RuntimeInfoDto.From).ToList());
    }
}