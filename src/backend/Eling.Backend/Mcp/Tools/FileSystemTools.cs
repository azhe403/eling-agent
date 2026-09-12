using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Core;
using Eling.Core.Exceptions;
using Eling.Core.FileSystem;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Bounded filesystem tools scoped to the project root. Thin wrapper over
/// <see cref="IFileSystemService"/>: clamps caller-controlled limits, maps
/// predictable failures to <c>{ ok: false, code }</c> JSON the agent can
/// branch on, and returns raw shapes on success (<c>file_read</c> returns
/// the plain file content). Auto-discovered by
/// <c>WithToolsFromAssembly()</c>.
/// </summary>
[McpServerToolType]
public sealed class FileSystemTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IFileSystemService _fs;
    private readonly ILogger<FileSystemTools>? _logger;

    public FileSystemTools(IFileSystemService fs, ILogger<FileSystemTools>? logger = null)
    {
        _fs = fs;
        _logger = logger;
    }

    [McpServerTool(Name = "path_test"), Description("Check whether a path exists under the project root and report its kind (file / directory / symlink) and size. Read-only; never throws for not-found.")]
    public string PathTest(
        [Description("Absolute or project-relative path.")] string path)
        => Execute(() => JsonSerializer.Serialize(_fs.TestPath(path), JsonOptions), nameof(PathTest));

    [McpServerTool(Name = "directory_create"), Description("Create a directory under the project root (and any missing parents), idempotent like mkdir -p. Returns whether it was created.")]
    public string CreateDirectory(
        [Description("Absolute or project-relative directory path.")] string path)
        => Execute(() => JsonSerializer.Serialize(_fs.CreateDirectory(path), JsonOptions), nameof(CreateDirectory));

    [McpServerTool(Name = "directory_list"), Description("List the contents of a directory under the project root. Flat (non-nested) entries; directories first, then files.")]
    public string ListDirectory(
        [Description("Absolute or project-relative directory path.")] string path,
        [Description("Recurse into subdirectories.")] bool recursive = false,
        [Description("Recursion depth cap, clamped to [0, 5].")] int maxDepth = 3,
        [Description("Optional name filter like '*.cs'.")] string? pattern = null)
        => Execute(() => JsonSerializer.Serialize(
            _fs.ListDirectory(path, recursive, Clamp(maxDepth, 0, 5), pattern), JsonOptions), nameof(ListDirectory));

    [McpServerTool(Name = "glob"), Description("Search for files/directories under a base path matching a glob pattern. Supports *, ?, ** and [abc]; matches files and directories; symlinks are never expanded.")]
    public string Glob(
        [Description("Absolute or project-relative root to search under.")] string basePath,
        [Description("Glob pattern matched against entry names, e.g. '**/*.cs'.")] string pattern,
        [Description("Recursion depth cap, clamped to [0, 10].")] int maxDepth = 5,
        [Description("Max entries returned, clamped to [1, 1000].")] int maxResults = 200)
        => Execute(() => JsonSerializer.Serialize(
            _fs.Glob(basePath, pattern, Clamp(maxDepth, 0, 10), Clamp(maxResults, 1, 1000)), JsonOptions), nameof(Glob));

    [McpServerTool(Name = "file_read"), Description("Read a text file under the project root. Whole file by default (plain content); with offset/limit returns a JSON envelope with line numbers and a truncation flag. Rejects binary files and files over the byte cap.")]
    public string ReadFile(
        [Description("Absolute or project-relative file path.")] string path,
        [Description("Byte cap, clamped to [1, 1048576].")] int maxBytes = 1048576,
        [Description("First line to return (1-based).")] int offset = 1,
        [Description("Max lines to return (0 = to end of file), clamped to [0, 5000].")] int limit = 0)
    {
        try
        {
            var result = _fs.ReadFile(path, Clamp(maxBytes, 1, 1048576), Math.Max(offset, 1), Clamp(limit, 0, 5000));
            if (offset <= 1 && limit <= 0)
            {
                return result.Content;
            }

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "file_read failed for '{Path}'", path);
            return ErrorJson(ex);
        }
    }

    [McpServerTool(Name = "file_write"), Description("Write a UTF-8 text file under the project root, creating parent directories on demand. Without overwrite=true an existing file is left untouched and reported as an error.")]
    public string WriteFile(
        [Description("Absolute or project-relative file path.")] string path,
        [Description("File content (UTF-8).")] string content,
        [Description("Replace the file when it already exists.")] bool overwrite = false)
        => Execute(() => JsonSerializer.Serialize(_fs.WriteFile(path, content, overwrite), JsonOptions), nameof(WriteFile));

    [McpServerTool(Name = "file_delete"), Description("Permanently delete a file under the project root. The sandbox root itself can never be deleted. Returns whether the file was deleted.")]
    public string DeleteFile(
        [Description("Absolute or project-relative file path.")] string path)
        => Execute(() => JsonSerializer.Serialize(_fs.DeleteFile(path), JsonOptions), nameof(DeleteFile));

    [McpServerTool(Name = "directory_delete"), Description("Permanently delete a directory under the project root. Non-empty directories require recursive=true. The sandbox root itself can never be deleted.")]
    public string DeleteDirectory(
        [Description("Absolute or project-relative directory path.")] string path,
        [Description("Delete a non-empty directory and everything under it.")] bool recursive = false)
        => Execute(() => JsonSerializer.Serialize(_fs.DeleteDirectory(path, recursive), JsonOptions), nameof(DeleteDirectory));

    [McpServerTool(Name = "file_move"), Description("Move or rename a file under the project root, creating destination parents on demand. Without overwrite=true an existing destination is left untouched.")]
    public string MoveFile(
        [Description("Absolute or project-relative source file path.")] string sourcePath,
        [Description("Absolute or project-relative destination file path.")] string destinationPath,
        [Description("Replace the destination when it already exists.")] bool overwrite = false)
        => Execute(() => JsonSerializer.Serialize(_fs.MoveFile(sourcePath, destinationPath, overwrite), JsonOptions), nameof(MoveFile));

    [McpServerTool(Name = "directory_move"), Description("Move or rename a directory under the project root, creating destination parents on demand. Cannot move a directory inside itself.")]
    public string MoveDirectory(
        [Description("Absolute or project-relative source directory path.")] string sourcePath,
        [Description("Absolute or project-relative destination directory path.")] string destinationPath,
        [Description("Replace the destination when it already exists.")] bool overwrite = false)
        => Execute(() => JsonSerializer.Serialize(_fs.MoveDirectory(sourcePath, destinationPath, overwrite), JsonOptions), nameof(MoveDirectory));

    [McpServerTool(Name = "file_copy"), Description("Copy a file under the project root, creating destination parents on demand. Without overwrite=true an existing destination is left untouched. Source is unchanged.")]
    public string CopyFile(
        [Description("Absolute or project-relative source file path.")] string sourcePath,
        [Description("Absolute or project-relative destination file path.")] string destinationPath,
        [Description("Replace the destination when it already exists.")] bool overwrite = false)
        => Execute(() => JsonSerializer.Serialize(_fs.CopyFile(sourcePath, destinationPath, overwrite), JsonOptions), nameof(CopyFile));

    [McpServerTool(Name = "directory_copy"), Description("Copy a directory tree under the project root, creating destination parents on demand. Cannot copy a directory inside itself. Source is unchanged.")]
    public string CopyDirectory(
        [Description("Absolute or project-relative source directory path.")] string sourcePath,
        [Description("Absolute or project-relative destination directory path.")] string destinationPath,
        [Description("Replace the destination when it already exists.")] bool overwrite = false)
        => Execute(() => JsonSerializer.Serialize(_fs.CopyDirectory(sourcePath, destinationPath, overwrite), JsonOptions), nameof(CopyDirectory));

    [McpServerTool(Name = "file_edit"), Description("Surgically replace text in a file under the project root. oldString must match exactly once unless replaceAll=true. CRLF/LF differences are tolerated. Returns the replacement count.")]
    public string EditFile(
        [Description("Absolute or project-relative file path.")] string path,
        [Description("Exact text to find. Include surrounding lines to make it unique.")] string oldString,
        [Description("Replacement text.")] string newString,
        [Description("Replace all occurrences instead of requiring a unique match.")] bool replaceAll = false,
        [Description("Byte cap, clamped to [1, 1048576].")] int maxBytes = 1048576)
        => Execute(() => JsonSerializer.Serialize(
            _fs.EditFile(path, oldString, newString, replaceAll, Clamp(maxBytes, 1, 1048576)), JsonOptions), nameof(EditFile));

    [McpServerTool(Name = "file_append"), Description("Append text to the end of a file under the project root, creating the file and parents when missing. Refuses binary files. Returns the new size.")]
    public string AppendFile(
        [Description("Absolute or project-relative file path.")] string path,
        [Description("Text to append (as-is, no newline added).")] string content)
        => Execute(() => JsonSerializer.Serialize(_fs.AppendFile(path, content), JsonOptions), nameof(AppendFile));

    [McpServerTool(Name = "file_search"), Description("Search file contents under a base path. Literal substring by default, regex with useRegex=true. Skips binary and oversized files. Returns file, line number, and trimmed line per hit.")]
    public string SearchFiles(
        [Description("Absolute or project-relative root to search under.")] string basePath,
        [Description("Substring or regex pattern to find.")] string pattern,
        [Description("Interpret pattern as a .NET regex (2s timeout per line).")] bool useRegex = false,
        [Description("Case-sensitive matching.")] bool caseSensitive = false,
        [Description("Optional name filter like '*.cs'.")] string? filePattern = null,
        [Description("Recursion depth cap, clamped to [0, 10].")] int maxDepth = 5,
        [Description("Max hits returned, clamped to [1, 1000].")] int maxResults = 100,
        [Description("Files larger than this are skipped, clamped to [1024, 5242880].")] int maxFileBytes = 1048576)
        => Execute(() => JsonSerializer.Serialize(
            _fs.SearchFiles(basePath, pattern, useRegex, caseSensitive, filePattern, Clamp(maxDepth, 0, 10), Clamp(maxResults, 1, 1000), Clamp(maxFileBytes, 1024, 5242880)),
            JsonOptions), nameof(SearchFiles));

    private string Execute(Func<string> call, string tool)
    {
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "{Tool} failed", tool);
            return ErrorJson(ex);
        }
    }

    private static string ErrorJson(Exception ex)
    {
        var code = ex switch
        {
            PathSandboxException => "sandbox_violation",
            FileNotFoundException or DirectoryNotFoundException => "not_found",
            PathAlreadyExistsException => "already_exists",
            DirectoryNotEmptyException => "directory_not_empty",
            OldStringNotFoundException => "old_string_not_found",
            AmbiguousMatchException => "multiple_matches",
            BinaryFileException => "binary_file",
            FileTooLargeException => "file_too_large",
            NotADirectoryException => "not_a_directory",
            NotAFileException => "not_a_file",
            ArgumentException => "invalid_argument",
            _ => "internal_error"
        };
        return JsonSerializer.Serialize(new { ok = false, error = ex.Message, code }, JsonOptions);
    }

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);
}
