using Eling.Backend.Dtos;
using Eling.Backend.Endpoints;
using Eling.Core.Codebase;
using Eling.Core.Memory;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Mvc;

namespace Eling.Backend.Controllers;

/// <summary>
/// The codebase page's file viewer: one indexed file's chunks, and that same
/// file read whole from disk.
/// </summary>
/// <remarks>
/// A controller rather than a minimal-API handler because the codebase group is
/// migrating off minimal APIs, and a new endpoint should not extend the style
/// being retired. Scope resolution is still delegated to
/// <see cref="CodebaseEndpoints.ResolveScope"/> so both styles read the index
/// the result list was drawn from.
/// </remarks>
[ApiController]
[Route("api/codebase/file")]
public sealed class CodebaseFileController(
    CodebaseIndexService index,
    CodebaseFileReader reader,
    RuntimeRegistry registry,
    ILogger<CodebaseFileController> logger) : ControllerBase
{
    /// <summary>
    /// Every indexed chunk for one file, in line order. The ranges overlap by
    /// design, so the list is retrieval metadata, not a reconstruction.
    /// </summary>
    [HttpGet("")]
    [ProducesResponseType<CodebaseFileDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CodebaseFileDetailResponse>> GetChunksAsync(
        [FromQuery] string? path,
        [FromQuery] string? scope = null,
        [FromQuery] string[]? project = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("A 'path' query parameter is required.");
        }

        try
        {
            var resolved = await CodebaseEndpoints.ResolveScopeAsync(index, registry, scope, project);
            var detail = await index.GetFileDetailAsync(path, resolved.Roots, cancellationToken);
            if (detail is null)
            {
                return NotFound($"No indexed file at '{path}' in the selected scope.");
            }

            return Ok(new CodebaseFileDetailResponse(
                detail.ProjectRoot,
                detail.Path,
                detail.Size,
                detail.LastIndexedAt,
                detail.Chunks.Select(c => new CodebaseChunkDto(c.StartLine, c.EndLine, c.Content)).ToList()));
        }
        catch (ArgumentException ex)
        {
            // Scope validation rejected a root. The message names the offender,
            // so pass it through instead of hiding it behind a generic failure.
            logger.LogWarning("Rejected codebase file scope: {Reason}", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Codebase file read failed for {Path} (scope={Scope}).", path, scope);
            return BadRequest("Failed to read the indexed file.");
        }
    }

    /// <summary>
    /// One workspace-relative file read from disk. Every outcome is a 200 with
    /// a status, because "this file is 4 MB" and "this file is binary" are
    /// answers the viewer asked for — only a rejected path is a bad request.
    /// </summary>
    /// <remarks>
    /// <c>project</c> is required here though the chunks endpoint treats it as
    /// optional: a disk read has to be told which single workspace to read
    /// from, and "all" has no meaning for one file.
    /// </remarks>
    [HttpGet("content")]
    [ProducesResponseType<CodebaseFileContentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CodebaseFileContentResponse>> GetContentAsync(
        [FromQuery] string? path,
        [FromQuery] string[]? project = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("A 'path' query parameter is required.");
        }

        try
        {
            // The same validation the other codebase endpoints apply, so the
            // exclusion gate holds for this read path too.
            var explicitRoots = ElingPaths.EnsureCodebaseRootsAllowed(project ?? []);
            if (explicitRoots.Count == 0)
            {
                return BadRequest("A 'project' query parameter naming the workspace root is required.");
            }

            var root = explicitRoots[0];
            var read = await reader.ReadAsync(root, path, cancellationToken);
            return Ok(new CodebaseFileContentResponse(
                root,
                path.Replace('\\', '/'),
                ToWireStatus(read.Status),
                read.Size,
                read.Content));
        }
        catch (ArgumentException ex)
        {
            // Either the root was rejected or the path escaped it. Both are a
            // caller naming a location it is not allowed to read.
            logger.LogWarning("Rejected codebase file content path: {Reason}", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Codebase file content read failed for {Path}.", path);
            return BadRequest("Failed to read the file.");
        }
    }

    /// <summary>
    /// The status enum as a stable lowercase wire value. Serializing the enum
    /// directly would couple the JSON contract to C# member names.
    /// </summary>
    private static string ToWireStatus(CodebaseFileReadStatus status) => status switch
    {
        CodebaseFileReadStatus.Ok => "ok",
        CodebaseFileReadStatus.NotFound => "notFound",
        CodebaseFileReadStatus.TooLarge => "tooLarge",
        CodebaseFileReadStatus.Binary => "binary",
        CodebaseFileReadStatus.Unreadable => "unreadable",
        _ => "unreadable",
    };
}
