namespace Eling.Backend.Dtos;

public record WorkspaceListResponse(List<string> Roots);

public record AddWorkspaceRequest(string Path);

public record FileListEntry(string Path, bool IsDirectory);

public record FileListResponse(List<FileListEntry> Entries);

public record FileReadResponse(string Content, bool Truncated);

public record WriteFileRequest(string Root, string Path, string Content);
