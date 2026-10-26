using System.Collections.Generic;

namespace Eling.Desktop.Models;

/// <summary>
/// The workspace list, pre-arranged in all three view modes, as returned by
/// GET /api/coordinator/workspaces.
/// </summary>
public sealed record WorkspaceTreesDto(
    IReadOnlyList<WorkspaceTreeRowDto> Rows,
    IReadOnlyList<WorkspaceTreeNodeDto> Flat,
    IReadOnlyList<WorkspaceTreeNodeDto> Repo,
    IReadOnlyList<WorkspaceTreeNodeDto> Folder);

public sealed record WorkspaceTreeRowDto(
    string Root,
    string Name,
    string? RepositoryRoot,
    bool Alive,
    int ProcessCount);

public sealed record WorkspaceTreeNodeDto(
    string Id,
    string Label,
    string Path,
    string? Root,
    bool Alive,
    IReadOnlyList<string> Roots,
    bool IsRepository,
    bool IsWorktree,
    IReadOnlyList<WorkspaceTreeNodeDto>? Children = null)
{
    public IReadOnlyList<WorkspaceTreeNodeDto> Children { get; init; } = Children ?? [];
}
