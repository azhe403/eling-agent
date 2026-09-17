namespace Eling.Desktop.Models;

public record WorkspaceListDto(List<string> Roots);

public record FileListEntryDto(string Path, bool IsDirectory);

public record FileListDto(List<FileListEntryDto> Entries);

public record FileReadDto(string Content, bool Truncated);

public record HostBrowseDto(string Path, string? Parent, List<FileListEntryDto> Entries);
