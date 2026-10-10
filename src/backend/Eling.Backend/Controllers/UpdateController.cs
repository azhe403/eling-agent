using Eling.Backend.Dtos;
using Eling.Backend.Updates;
using Microsoft.AspNetCore.Mvc;

namespace Eling.Backend.Controllers;

/// <summary>
/// Update status for the dashboard toast. Served from the cached checker, so
/// this endpoint never performs network traffic itself.
/// </summary>
[ApiController]
[Route("api/update")]
public sealed class UpdateController(IUpdateChecker checker) : ControllerBase
{
    /// <summary>
    /// Returns the last known update status: current versus latest release and
    /// whether an update is available.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType<UpdateStatusDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UpdateStatusDto>> GetStatusAsync(CancellationToken cancellationToken)
        => Ok(UpdateStatusDto.FromStatus(await checker.GetStatusAsync(cancellationToken)));
}
