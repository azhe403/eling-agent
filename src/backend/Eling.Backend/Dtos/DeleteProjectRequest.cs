namespace Eling.Backend.Dtos;

public sealed record DeleteProjectRequest(
    string WorkspaceRoot,
    bool DeleteCodebaseIndex = false,
    bool DeleteDotEling = false);
