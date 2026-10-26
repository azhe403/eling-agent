using Eling.Core.Workspaces;

namespace Eling.Backend.Dtos;

/// <summary>
/// Turns the endpoint's rows into the three arrangements, by calling the one
/// implementation in <see cref="WorkspaceTree"/>. Kept apart from the DTOs so
/// the wire shape stays readable on its own.
/// </summary>
internal static class WorkspaceTreeDtoMapper
{
    public static WorkspaceTreesDto Map(IReadOnlyList<WorkspaceTreeRowDto> rows)
    {
        var coreRows = rows
            .Select(r => new WorkspaceTreeRow(r.Root, r.Alive, r.RepositoryRoot))
            .ToList();

        return new WorkspaceTreesDto(
            rows,
            Arrange(coreRows, WorkspaceGroupMode.Flat),
            Arrange(coreRows, WorkspaceGroupMode.Repo),
            Arrange(coreRows, WorkspaceGroupMode.Folder));
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
