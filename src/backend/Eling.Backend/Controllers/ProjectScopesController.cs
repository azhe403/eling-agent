using Eling.Backend.Dtos;
using Eling.Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Eling.Backend.Controllers;

/// <summary>
/// The memory scopes this workspace can read and write, for the dashboard's
/// memory scope picker.
/// </summary>
/// <remarks>
/// Deliberately not part of <see cref="CoordinatorController"/>: that endpoint
/// answers "which workspaces have processes", which is the codebase question.
/// This one answers "which scopes exist", which is a walk of the <c>.eling</c>
/// chain plus the registry. The two lists disagree — an ancestor scope nobody is
/// running in belongs here and not there — and reading both from one endpoint is
/// what made the picker unable to show a scope a save could legitimately target.
/// </remarks>
[ApiController]
[Route("api/project/scopes")]
public sealed class ProjectScopesController(ProjectScopeCatalog catalog) : ControllerBase
{
    /// <summary>
    /// Scopes reachable from <paramref name="cwd"/> (default: this backend's
    /// working directory). Chain levels first with <c>relation</c> of
    /// <c>own</c>/<c>ancestor</c>, then other known scopes as <c>registered</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ProjectScopeListDto>> GetScopesAsync(
        [FromQuery] string? cwd = null,
        CancellationToken cancellationToken = default)
        => Ok(await catalog.ListAsync(cwd ?? Directory.GetCurrentDirectory(), cancellationToken));
}