namespace Eling.Backend.Dtos;

public record HostBrowseResponse(string Path, string? Parent, List<FileListEntry> Entries);
