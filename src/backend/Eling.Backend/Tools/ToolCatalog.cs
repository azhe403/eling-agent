namespace Eling.Backend.Tools;

/// <summary>
/// Static catalog of every MCP tool Eling exposes, grouped for the dashboard
/// and for group-based policy toggles. Descriptions stay short: the full
/// contracts live on the <c>[McpServerTool]</c> attributes.
/// </summary>
public static class ToolCatalog
{
    public const string MemoryGroup = "memory";
    public const string CodebaseGroup = "codebase";
    public const string FilesystemGroup = "filesystem";

    public static readonly IReadOnlyList<ToolDefinition> All = new List<ToolDefinition>
    {
        new("memory_recall", MemoryGroup, "Recall relevant memories and outstanding intentions for the current task."),
        new("memory_save", MemoryGroup, "Save a durable memory to the knowledge store."),
        new("memory_get", MemoryGroup, "Retrieve a memory by its ID."),
        new("memory_list", MemoryGroup, "List memories, optionally filtered by status."),
        new("memory_search", MemoryGroup, "Search memories by keyword query."),
        new("memory_delete", MemoryGroup, "Delete a memory by its ID."),
        new("memory_rebuild_index", MemoryGroup, "Rebuild the search index from all stored memories."),
        new("memory_maintenance", MemoryGroup, "Run on-demand memory maintenance (dedup, merge, cleanup)."),
        new("memory_init_project", MemoryGroup, "Create a project .eling directory after user consent."),
        new("memory_project_status", MemoryGroup, "Report the project-scope posture of the working directory."),
        new("memory_project_scope_policy", MemoryGroup, "Set a machine-local project-scope policy decision."),
        new("memory_copy_to_project", MemoryGroup, "Copy a memory to the current project scope."),
        new("memory_promote_to_global", MemoryGroup, "Promote a project memory to global scope."),
        new("tools_policy", MemoryGroup, "Inspect or modify the enabled state of MCP tools in real time."),
        new("codebase_status", CodebaseGroup, "Report codebase index stats: files, chunks, watcher state."),
        new("codebase_search", CodebaseGroup, "Search the codebase index and return path, lines, and snippet."),
        new("codebase_index", CodebaseGroup, "Build or refresh the codebase search index."),
        new("workspace_root", FilesystemGroup, "Return the absolute sandbox root of this server."),
        new("path_test", FilesystemGroup, "Check whether a path exists and report its kind and size."),
        new("directory_create", FilesystemGroup, "Create a directory under the project root."),
        new("directory_list", FilesystemGroup, "List the contents of a directory."),
        new("glob", FilesystemGroup, "Find files and directories matching a glob pattern."),
        new("file_read", FilesystemGroup, "Read a text file under the project root."),
        new("file_read_any", FilesystemGroup, "Read any file on disk with no sandbox restriction."),
        new("file_write", FilesystemGroup, "Write a UTF-8 text file under the project root."),
        new("file_delete", FilesystemGroup, "Permanently delete a file under the project root."),
        new("directory_delete", FilesystemGroup, "Permanently delete a directory under the project root."),
        new("file_move", FilesystemGroup, "Move or rename a file under the project root."),
        new("directory_move", FilesystemGroup, "Move or rename a directory under the project root."),
        new("file_copy", FilesystemGroup, "Copy a file under the project root."),
        new("directory_copy", FilesystemGroup, "Copy a directory tree under the project root."),
        new("file_edit", FilesystemGroup, "Surgically replace text in a file under the project root."),
        new("file_append", FilesystemGroup, "Append text to the end of a file under the project root."),
        new("file_search", FilesystemGroup, "Search file contents under a base path."),
    }.AsReadOnly();

    public static bool Exists(string toolName)
        => All.Any(tool => string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> MembersOf(string group)
        => All
            .Where(tool => string.Equals(tool.Group, group, StringComparison.OrdinalIgnoreCase))
            .Select(tool => tool.Name)
            .ToList()
            .AsReadOnly();
}
