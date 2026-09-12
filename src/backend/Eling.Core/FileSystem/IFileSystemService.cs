namespace Eling.Core.FileSystem;

/// <summary>
/// Bounded filesystem operations scoped to a single sandbox root. Every
/// method resolves its input under the root and throws
/// <see cref="PathSandboxException"/> when the resolved path escapes it.
/// </summary>
public interface IFileSystemService
{
    PathInfo TestPath(string path);
    DirectoryCreateResult CreateDirectory(string path);
    IReadOnlyList<DirectoryEntry> ListDirectory(string path, bool recursive = false, int maxDepth = 3, string? pattern = null);
    GlobResult Glob(string basePath, string pattern, int maxDepth = 5, int maxResults = 200);
    FileReadResult ReadFile(string path, int maxBytes = 1048576, int offset = 1, int limit = 0);
    FileWriteResult WriteFile(string path, string content, bool overwrite = false);
    DeleteResult DeleteFile(string path);
    DeleteResult DeleteDirectory(string path, bool recursive = false);
    MoveResult MoveFile(string sourcePath, string destinationPath, bool overwrite = false);
    MoveResult MoveDirectory(string sourcePath, string destinationPath, bool overwrite = false);
    CopyResult CopyFile(string sourcePath, string destinationPath, bool overwrite = false);
    CopyResult CopyDirectory(string sourcePath, string destinationPath, bool overwrite = false);
    FileEditResult EditFile(string path, string oldString, string newString, bool replaceAll = false, int maxBytes = 1048576);
    FileAppendResult AppendFile(string path, string content);
    ContentSearchResult SearchFiles(string basePath, string pattern, bool useRegex = false, bool caseSensitive = false, string? filePattern = null, int maxDepth = 5, int maxResults = 100, int maxFileBytes = 1048576);
}
