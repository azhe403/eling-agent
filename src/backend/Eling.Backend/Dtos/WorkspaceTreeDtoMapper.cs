using Eling.Core.Workspaces;

namespace Eling.Backend.Dtos;

/// <summary>
/// Turns the endpoint's rows into a single arrangement for the requested mode,
/// by calling the one implementation in <see cref="WorkspaceTree"/>. Kept apart
/// from the DTOs so the wire shape stays readable on its own.
/// </summary>
public static class WorkspaceTreeDtoMapper
{
    public static WorkspaceTreesDto Map(
        IReadOnlyList<WorkspaceTreeRowDto> rows,
        WorkspaceGroupMode mode)
    {
        var coreRows = rows
            .Select(r => new WorkspaceTreeRow(r.Root, r.Alive, r.RepositoryRoot))
            .ToList();

        return new WorkspaceTreesDto(
            rows,
            mode.ToWireValue(),
            Arrange(coreRows, mode));
    }

    private static List<WorkspaceTreeNodeDto> Arrange(
        IReadOnlyList<WorkspaceTreeRow> rows,
        WorkspaceGroupMode mode)
    {
        return WorkspaceTree.Build(rows, mode).Select(Map).ToList();
    }

    private static WorkspaceTreeNodeDto Map(WorkspaceTreeNode node)
    {
        return new WorkspaceTreeNodeDto(
            node.Id,
            node.Label,
            node.Path,
            node.Root,
            node.Alive,
            node.Roots,
            node.IsRepository,
            node.IsWorktree,
            node.Children.Select(Map).ToList());
    }
}
