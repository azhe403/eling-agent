using System.Collections.Generic;

namespace Eling.Desktop.Models;

/// <summary>
/// The workspace list, pre-arranged for ONE view mode, as returned by
/// GET /api/coordinator/workspaces?group=flat|repo|folder.
/// </summary>
public sealed record WorkspaceTreesDto(
    IReadOnlyList<WorkspaceTreeRowDto> Rows,
    string Group,
    IReadOnlyList<WorkspaceTreeNodeDto> Nodes);

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
