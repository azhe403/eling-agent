using Eling.Backend.Dtos;
using Eling.Backend.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Eling.Backend.Controllers;

/// <summary>
/// Host-level information the dashboard chrome needs. Kept on a controller
/// while the rest of the API is still minimal APIs; see DashboardServices for
/// the JSON options that keep both styles serializing identically.
/// </summary>
[ApiController]
[Route("api/system")]
public sealed class SystemController(GitIdentityService identity) : ControllerBase
{
    /// <summary>
    /// Resolves the name and email shown in the dashboard chrome from the
    /// global git config. The name falls back to <c>anonymous</c> when git has
    /// no <c>user.name</c>; the email is simply left empty.
    /// </summary>
    [HttpGet("identity")]
    [ProducesResponseType<GitIdentityDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<GitIdentityDto>> GetIdentityAsync(CancellationToken cancellationToken)
        => Ok(await identity.GetIdentityAsync(cancellationToken));
}
