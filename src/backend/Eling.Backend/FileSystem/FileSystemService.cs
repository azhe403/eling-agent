using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using Eling.Core;
using Eling.Core.Exceptions;
using Eling.Core.FileSystem;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.FileSystem;

/// <summary>
/// Default <see cref="IFileSystemService"/> backed by <c>System.IO</c>.
/// The sandbox root is injected (production wires
/// <c>ProjectScope.Root</c>), so unit tests can point at a temp dir.
/// Symlinks are reported but never followed across the boundary.
/// </summary>
public sealed class FileSystemService : IFileSystemService
{
    private const int BinaryProbeBytes = 8192;

    private readonly string _rootPath;
    private readonly string _rootWithSeparator;
    private readonly StringComparison _rootComparison;
    private readonly int _defaultReadMaxBytes;
    private readonly int _defaultMaxDepth;
    private readonly ILogger<FileSystemService>? _logger;

    public FileSystemService(string rootPath, int defaultReadMaxBytes = 1048576, int defaultMaxDepth = 3, ILogger<FileSystemService>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _rootWithSeparator = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        _rootComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _defaultReadMaxBytes = defaultReadMaxBytes;
        _defaultMaxDepth = defaultMaxDepth;
        _logger = logger;
    }

    public PathInfo TestPath(string path)
    {
        var full = ResolvePath(path);
        if (IsSymlink(full))
        {
            return new PathInfo(full, true, PathKind.Symlink, null);
        }

        if (File.Exists(full))
        {
            return new PathInfo(full, true, PathKind.File, new FileInfo(full).Length);
        }

        if (Directory.Exists(full))
        {
            return new PathInfo(full, true, PathKind.Directory, null);
        }

        return new PathInfo(full, false, PathKind.NotFound, null);
    }

    public DirectoryCreateResult CreateDirectory(string path)
    {
        var full = ResolvePath(path);
        if (File.Exists(full) || IsFileLink(full))
        {
            throw new NotADirectoryException(full);
        }

        if (Directory.Exists(full))
        {
            return new DirectoryCreateResult(full, false);
        }

        try
        {
            Directory.CreateDirectory(full);
            return new DirectoryCreateResult(full, true);
        }
        catch (IOException ex)
        {
            // A parent component is a file: surface as not-found so the
            // agent gets an actionable code instead of a raw IO error.
            _logger?.LogWarning(ex, "CreateDirectory failed for '{Path}'", full);
            throw new DirectoryNotFoundException($"Could not create '{full}': a parent component is not a directory.", ex);
        }
    }

    public IReadOnlyList<DirectoryEntry> ListDirectory(string path, bool recursive = false, int maxDepth = 3, string? pattern = null)
    {
        var full = ResolvePath(path);
        if (File.Exists(full) || IsFileLink(full))
        {
            throw new NotADirectoryException(full);
        }

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Directory '{full}' does not exist.");
        }

        var requestedDepth = maxDepth < 0 ? _defaultMaxDepth : maxDepth;
        var depth = Math.Max(0, recursive ? requestedDepth : 0);
        var entries = recursive
            ? EnumerateRecursive(full, depth)
            : EnumerateSingleLevel(full);

        if (!string.IsNullOrWhiteSpace(pattern))
        {
            entries = entries.Where(e => FileSystemName.MatchesWin32Expression(pattern, e.Name)).ToList();
        }

        return SortEntries(entries);
    }

    public GlobResult Glob(string basePath, string pattern, int maxDepth = 5, int maxResults = 200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var baseFull = ResolvePath(basePath);
        if (File.Exists(baseFull) || IsFileLink(baseFull))
        {
            throw new NotADirectoryException(baseFull);
        }

        if (!Directory.Exists(baseFull))
        {
            throw new DirectoryNotFoundException($"Directory '{baseFull}' does not exist.");
        }

        var depth = Math.Max(0, maxDepth);
        var cap = Math.Max(1, maxResults);
        var segments = pattern.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var matches = new List<DirectoryEntry>();

        foreach (var entry in EnumerateRecursive(baseFull, depth))
        {
            var relative = Path.GetRelativePath(baseFull, entry.Path);
            var relSegments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (MatchSegments(segments, relSegments))
            {
                matches.Add(entry);
                if (matches.Count > cap)
                {
                    break;
                }
            }
        }

        var truncated = matches.Count > cap;
        var sorted = matches
            .Take(cap)
            .OrderBy(e => e.Path, GetPathComparer())
            .ToList()
            .AsReadOnly();
        return new GlobResult(baseFull, pattern, sorted.Count, truncated, sorted);
    }

    public FileReadResult ReadFile(string path, int maxBytes = 1048576, int offset = 1, int limit = 0)
    {
        var full = ResolvePath(path);
        if (Directory.Exists(full) && !File.Exists(full))
        {
            throw new NotAFileException(full);
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"File '{full}' does not exist.", full);
        }

        var cap = maxBytes > 0 ? maxBytes : _defaultReadMaxBytes;
        var size = new FileInfo(full).Length;
        if (size > cap)
        {
            throw new FileTooLargeException(full, size, cap);
        }

        byte[] bytes;
        try
        {
            bytes = ReadTextBytes(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "ReadFile failed for '{Path}'", full);
            throw;
        }

        var content = StripBom(Encoding.UTF8.GetString(bytes));
        if (offset <= 1 && limit <= 0)
        {
            var total = CountLines(content);
            return new FileReadResult(full, size, content, 1, total, total, false);
        }

        var start = Math.Max(offset, 1);
        var (slice, end, totalLines) = SliceLines(content, start, limit);
        return new FileReadResult(full, size, slice, start, end, totalLines, end < totalLines);
    }

    public FileWriteResult WriteFile(string path, string content, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        var full = ResolvePath(path);
        if (Directory.Exists(full) && !File.Exists(full))
        {
            throw new NotAFileException(full);
        }

        var existed = File.Exists(full);
        if (existed && !overwrite)
        {
            throw new FileAlreadyExistsException(full);
        }

        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "WriteFile failed for '{Path}'", full);
            throw;
        }

        return new FileWriteResult(full, new FileInfo(full).Length, existed);
    }

    public DeleteResult DeleteFile(string path)
    {
        var full = ResolvePath(path);
        RejectSandboxRoot(full, "delete", nameof(path));
        if (IsDirectoryLink(full))
        {
            throw new NotAFileException(full);
        }

        if (IsFileLink(full))
        {
            File.Delete(full);
            return new DeleteResult(full, true);
        }

        if (!File.Exists(full))
        {
            if (Directory.Exists(full))
            {
                throw new NotAFileException(full);
            }

            throw new FileNotFoundException($"File '{full}' does not exist.", full);
        }

        try
        {
            File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "DeleteFile failed for '{Path}'", full);
            throw;
        }

        return new DeleteResult(full, true);
    }

    public DeleteResult DeleteDirectory(string path, bool recursive = false)
    {
        var full = ResolvePath(path);
        RejectSandboxRoot(full, "delete", nameof(path));
        if (IsFileLink(full))
        {
            throw new NotADirectoryException(full);
        }

        if (IsDirectoryLink(full))
        {
            Directory.Delete(full, recursive: false);
            return new DeleteResult(full, true);
        }

        if (File.Exists(full) || IsFileLink(full))
        {
            throw new NotADirectoryException(full);
        }

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Directory '{full}' does not exist.");
        }

        if (!recursive && Directory.EnumerateFileSystemEntries(full).Any())
        {
            throw new DirectoryNotEmptyException(full);
        }

        try
        {
            Directory.Delete(full, recursive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "DeleteDirectory failed for '{Path}'", full);
            throw;
        }

        return new DeleteResult(full, true);
    }

    public MoveResult MoveFile(string sourcePath, string destinationPath, bool overwrite = false)
    {
        var srcFull = ResolvePath(sourcePath);
        var dstFull = ResolvePath(destinationPath);
        RejectSandboxRoot(srcFull, "move", nameof(sourcePath));
        RejectSandboxRoot(dstFull, "move to", nameof(destinationPath));
        if (string.Equals(srcFull, dstFull, _rootComparison))
        {
            throw new ArgumentException("Source and destination are the same path.", nameof(destinationPath));
        }

        if (!File.Exists(srcFull))
        {
            if (Directory.Exists(srcFull))
            {
                throw new NotAFileException(srcFull);
            }

            throw new FileNotFoundException($"File '{srcFull}' does not exist.", srcFull);
        }

        if (Directory.Exists(dstFull) && !File.Exists(dstFull))
        {
            throw new NotAFileException(dstFull);
        }

        var overwrote = File.Exists(dstFull);
        if (overwrote && !overwrite)
        {
            throw new FileAlreadyExistsException(dstFull);
        }

        var parent = Path.GetDirectoryName(dstFull);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            File.Move(srcFull, dstFull, overwrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "MoveFile failed from '{Source}' to '{Destination}'", srcFull, dstFull);
            throw;
        }

        return new MoveResult(srcFull, dstFull, overwrote);
    }

    public MoveResult MoveDirectory(string sourcePath, string destinationPath, bool overwrite = false)
    {
        var srcFull = ResolvePath(sourcePath);
        var dstFull = ResolvePath(destinationPath);
        RejectSandboxRoot(srcFull, "move", nameof(sourcePath));
        RejectSandboxRoot(dstFull, "move to", nameof(destinationPath));
        if (string.Equals(srcFull, dstFull, _rootComparison))
        {
            throw new ArgumentException("Source and destination are the same path.", nameof(destinationPath));
        }

        if (File.Exists(srcFull) || IsFileLink(srcFull))
        {
            throw new NotADirectoryException(srcFull);
        }

        if (!Directory.Exists(srcFull))
        {
            throw new DirectoryNotFoundException($"Directory '{srcFull}' does not exist.");
        }

        if (dstFull.StartsWith(srcFull + Path.DirectorySeparatorChar, _rootComparison))
        {
            throw new ArgumentException("Cannot move a directory inside itself.", nameof(destinationPath));
        }

        var overwrote = Directory.Exists(dstFull) || File.Exists(dstFull);
        if (overwrote && !overwrite)
        {
            throw new PathAlreadyExistsException(dstFull);
        }

        var parent = Path.GetDirectoryName(dstFull);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            if (overwrote)
            {
                if (IsDirectoryLink(dstFull))
                {
                    Directory.Delete(dstFull, recursive: false);
                }
                else if (IsFileLink(dstFull) || !Directory.Exists(dstFull))
                {
                    File.Delete(dstFull);
                }
                else
                {
                    Directory.Delete(dstFull, recursive: true);
                }
            }

            Directory.Move(srcFull, dstFull);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "MoveDirectory failed from '{Source}' to '{Destination}'", srcFull, dstFull);
            throw;
        }

        return new MoveResult(srcFull, dstFull, overwrote);
    }

    public CopyResult CopyFile(string sourcePath, string destinationPath, bool overwrite = false)
    {
        var srcFull = ResolvePath(sourcePath);
        var dstFull = ResolvePath(destinationPath);
        RejectSandboxRoot(srcFull, "copy", nameof(sourcePath));
        RejectSandboxRoot(dstFull, "copy to", nameof(destinationPath));
        if (string.Equals(srcFull, dstFull, _rootComparison))
        {
            throw new ArgumentException("Source and destination are the same path.", nameof(destinationPath));
        }

        if (!File.Exists(srcFull))
        {
            if (Directory.Exists(srcFull))
            {
                throw new NotAFileException(srcFull);
            }

            throw new FileNotFoundException($"File '{srcFull}' does not exist.", srcFull);
        }

        if (Directory.Exists(dstFull) && !File.Exists(dstFull))
        {
            throw new NotAFileException(dstFull);
        }

        var overwrote = File.Exists(dstFull);
        if (overwrote && !overwrite)
        {
            throw new FileAlreadyExistsException(dstFull);
        }

        var parent = Path.GetDirectoryName(dstFull);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            File.Copy(srcFull, dstFull, overwrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "CopyFile failed from '{Source}' to '{Destination}'", srcFull, dstFull);
            throw;
        }

        return new CopyResult(srcFull, dstFull, overwrote, 1);
    }

    public CopyResult CopyDirectory(string sourcePath, string destinationPath, bool overwrite = false)
    {
        var srcFull = ResolvePath(sourcePath);
        var dstFull = ResolvePath(destinationPath);
        RejectSandboxRoot(srcFull, "copy", nameof(sourcePath));
        RejectSandboxRoot(dstFull, "copy to", nameof(destinationPath));
        if (string.Equals(srcFull, dstFull, _rootComparison))
        {
            throw new ArgumentException("Source and destination are the same path.", nameof(destinationPath));
        }

        if (File.Exists(srcFull) || IsFileLink(srcFull))
        {
            throw new NotADirectoryException(srcFull);
        }

        if (!Directory.Exists(srcFull))
        {
            throw new DirectoryNotFoundException($"Directory '{srcFull}' does not exist.");
        }

        if (dstFull.StartsWith(srcFull + Path.DirectorySeparatorChar, _rootComparison))
        {
            throw new ArgumentException("Cannot copy a directory inside itself.", nameof(destinationPath));
        }

        var overwrote = Directory.Exists(dstFull) || File.Exists(dstFull);
        if (overwrote && !overwrite)
        {
            throw new PathAlreadyExistsException(dstFull);
        }

        var parent = Path.GetDirectoryName(dstFull);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            if (overwrote)
            {
                if (IsDirectoryLink(dstFull))
                {
                    Directory.Delete(dstFull, recursive: false);
                }
                else if (IsFileLink(dstFull) || !Directory.Exists(dstFull))
                {
                    File.Delete(dstFull);
                }
                else
                {
                    Directory.Delete(dstFull, recursive: true);
                }
            }

            Directory.CreateDirectory(dstFull);
            var copied = CopyTree(srcFull, dstFull);
            return new CopyResult(srcFull, dstFull, overwrote, copied);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "CopyDirectory failed from '{Source}' to '{Destination}'", srcFull, dstFull);
            throw;
        }
    }

    private static int CopyTree(string sourceDir, string destinationDir)
    {
        var count = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDir))
        {
            var target = Path.Combine(destinationDir, Path.GetFileName(entry));
            if (IsSymlink(entry))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(target);
                count += CopyTree(entry, target);
            }
            else
            {
                File.Copy(entry, target, overwrite: false);
                count++;
            }
        }

        return count;
    }

    public FileEditResult EditFile(string path, string oldString, string newString, bool replaceAll = false, int maxBytes = 1048576)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldString);
        ArgumentNullException.ThrowIfNull(newString);
        var full = ResolvePath(path);
        if (Directory.Exists(full) && !File.Exists(full))
        {
            throw new NotAFileException(full);
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"File '{full}' does not exist.", full);
        }

        var cap = maxBytes > 0 ? maxBytes : _defaultReadMaxBytes;
        var size = new FileInfo(full).Length;
        if (size > cap)
        {
            throw new FileTooLargeException(full, size, cap);
        }

        var bytes = ReadTextBytes(full);
        var content = StripBom(Encoding.UTF8.GetString(bytes));
        var crlf = content.Contains("\r\n", StringComparison.Ordinal);
        var replacement = crlf
            ? newString.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n")
            : newString.Replace("\r\n", "\n");
        var (normalized, map) = NormalizeNewlines(content);
        var needle = oldString.Replace("\r\n", "\n");

        var matches = FindAll(normalized, needle);
        if (matches.Count == 0)
        {
            throw new OldStringNotFoundException(full);
        }

        if (matches.Count > 1 && !replaceAll)
        {
            throw new AmbiguousMatchException(full, matches.Count);
        }

        var targets = replaceAll ? matches : matches.GetRange(0, 1);
        var builder = new StringBuilder(content);
        for (var i = targets.Count - 1; i >= 0; i--)
        {
            var start = map[targets[i]];
            var length = map[targets[i] + needle.Length] - start;
            builder.Remove(start, length);
            builder.Insert(start, replacement);
        }

        try
        {
            File.WriteAllText(full, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "EditFile failed for '{Path}'", full);
            throw;
        }

        return new FileEditResult(full, new FileInfo(full).Length, targets.Count);
    }

    public FileAppendResult AppendFile(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var full = ResolvePath(path);
        RejectSandboxRoot(full, "append to", nameof(path));
        if (Directory.Exists(full) && !File.Exists(full))
        {
            throw new NotAFileException(full);
        }

        var created = !File.Exists(full);
        if (!created)
        {
            try
            {
                ReadTextBytes(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "AppendFile rejected '{Path}'", full);
                throw;
            }
        }

        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            File.AppendAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "AppendFile failed for '{Path}'", full);
            throw;
        }

        return new FileAppendResult(full, new FileInfo(full).Length, created);
    }

    public ContentSearchResult SearchFiles(string basePath, string pattern, bool useRegex = false, bool caseSensitive = false, string? filePattern = null, int maxDepth = 5, int maxResults = 100, int maxFileBytes = 1048576)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var baseFull = ResolvePath(basePath);
        if (File.Exists(baseFull) || IsFileLink(baseFull))
        {
            throw new NotADirectoryException(baseFull);
        }

        if (!Directory.Exists(baseFull))
        {
            throw new DirectoryNotFoundException($"Directory '{baseFull}' does not exist.");
        }

        Regex? regex = null;
        if (useRegex)
        {
            try
            {
                regex = new Regex(
                    pattern,
                    (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.Compiled,
                    TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                _logger?.LogWarning(ex, "SearchFiles rejected invalid pattern '{Pattern}'", pattern);
                throw new ArgumentException($"Invalid search pattern: {ex.Message}", nameof(pattern), ex);
            }
        }

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var depth = Math.Max(0, maxDepth);
        var cap = Math.Max(1, maxResults);
        var fileCap = maxFileBytes > 0 ? maxFileBytes : _defaultReadMaxBytes;
        var matches = new List<ContentMatch>();

        foreach (var entry in EnumerateRecursive(baseFull, depth))
        {
            if (entry.Kind != PathKind.File)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(filePattern) &&
                !FileSystemName.MatchesWin32Expression(filePattern, entry.Name))
            {
                continue;
            }

            if (new FileInfo(entry.Path).Length > fileCap || IsBinaryFile(entry.Path))
            {
                continue;
            }

            string text;
            try
            {
                text = StripBom(Encoding.UTF8.GetString(File.ReadAllBytes(entry.Path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "SearchFiles skipped unreadable file '{Path}'", entry.Path);
                continue;
            }

            var lineNumber = 0;
            foreach (var rawLine in text.Split('\n'))
            {
                lineNumber++;
                var line = rawLine.TrimEnd('\r');
                var hit = regex is not null ? SafeIsMatch(regex, line, entry.Path) : line.Contains(pattern, comparison);
                if (!hit)
                {
                    continue;
                }

                matches.Add(new ContentMatch(entry.Path, lineNumber, line.Length > 500 ? line[..500] : line));
                if (matches.Count > cap)
                {
                    break;
                }
            }

            if (matches.Count > cap)
            {
                break;
            }
        }

        var truncated = matches.Count > cap;
        var page = matches
            .Take(cap)
            .OrderBy(m => m.Path, GetPathComparer())
            .ThenBy(m => m.LineNumber)
            .ToList()
            .AsReadOnly();
        return new ContentSearchResult(baseFull, pattern, page.Count, truncated, page);
    }

    private bool SafeIsMatch(Regex regex, string line, string path)
    {
        try
        {
            return regex.IsMatch(line);
        }
        catch (RegexMatchTimeoutException ex)
        {
            _logger?.LogWarning(ex, "SearchFiles pattern timed out on '{Path}'", path);
            return false;
        }
    }

    private static byte[] ReadTextBytes(string full)
    {
        var bytes = File.ReadAllBytes(full);
        var probeLength = Math.Min(bytes.Length, BinaryProbeBytes);
        for (var i = 0; i < probeLength; i++)
        {
            if (bytes[i] == 0)
            {
                throw new BinaryFileException(full);
            }
        }

        return bytes;
    }

    private static string StripBom(string text)
        => text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;

    private static bool IsBinaryFile(string full)
    {
        try
        {
            var probe = new byte[BinaryProbeBytes];
            using var stream = File.OpenRead(full);
            var read = stream.Read(probe, 0, probe.Length);
            for (var i = 0; i < read; i++)
            {
                if (probe[i] == 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static (string Normalized, List<int> Map) NormalizeNewlines(string content)
    {
        var normalized = new StringBuilder(content.Length);
        var map = new List<int>(content.Length + 1);
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
            {
                normalized.Append('\n');
                map.Add(i);
                i++;
                continue;
            }

            normalized.Append(content[i]);
            map.Add(i);
        }

        map.Add(content.Length);
        return (normalized.ToString(), map);
    }

    private static List<int> FindAll(string text, string needle)
    {
        var matches = new List<int>();
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            matches.Add(index);
            index += needle.Length;
        }

        return matches;
    }

    private static List<string> SplitLines(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var lines = content.Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static int CountLines(string content) => SplitLines(content).Count;

    private static (string Slice, int EndLine, int TotalLines) SliceLines(string content, int start, int limit)
    {
        var lines = SplitLines(content);
        var total = lines.Count;
        var startIndex = start - 1;
        if (startIndex >= total)
        {
            return ("", start - 1, total);
        }

        var take = limit <= 0 ? total - startIndex : Math.Min(limit, total - startIndex);
        var slice = string.Join('\n', lines.GetRange(startIndex, take).Select(l => l.TrimEnd('\r')));
        return (slice, startIndex + take, total);
    }

    private void RejectSandboxRoot(string full, string verb, string paramName)
    {
        if (string.Equals(
                full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                _rootComparison))
        {
            throw new ArgumentException($"Cannot {verb} the sandbox root itself.", paramName);
        }
    }

    internal string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be empty.", nameof(path));
        }

        var full = Path.GetFullPath(Path.Combine(_rootPath, path));
        if (!full.StartsWith(_rootWithSeparator, _rootComparison) &&
            !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), _rootComparison))
        {
            throw new PathSandboxException(full, _rootPath);
        }

        return full;
    }

    private static bool IsFileLink(string full)
        => TryGetLinkKind(full, out var isDirectory) && !isDirectory;

    private static bool IsDirectoryLink(string full)
        => TryGetLinkKind(full, out var isDirectory) && isDirectory;

    private static bool TryGetLinkKind(string full, out bool isDirectory)
    {
        try
        {
            var attributes = File.GetAttributes(full);
            isDirectory = (attributes & FileAttributes.Directory) != 0;
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            isDirectory = false;
            return false;
        }
    }

    private static bool IsSymlink(string full)
        => TryGetLinkKind(full, out _);

    private static DirectoryEntry ToEntry(string full)
    {
        var name = Path.GetFileName(full);
        if (IsSymlink(full))
        {
            return new DirectoryEntry(full, name, PathKind.Symlink, null);
        }

        if (File.Exists(full))
        {
            return new DirectoryEntry(full, name, PathKind.File, new FileInfo(full).Length);
        }

        if (Directory.Exists(full))
        {
            return new DirectoryEntry(full, name, PathKind.Directory, null);
        }

        return new DirectoryEntry(full, name, PathKind.Other, null);
    }

    private static IReadOnlyList<DirectoryEntry> EnumerateSingleLevel(string dir)
    {
        var result = new List<DirectoryEntry>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            result.Add(ToEntry(entry));
        }

        return result;
    }

    private static IReadOnlyList<DirectoryEntry> EnumerateRecursive(string dir, int maxDepth)
        => EnumerateRecursive(dir, maxDepth, 0);

    private static List<DirectoryEntry> EnumerateRecursive(string dir, int maxDepth, int depth)
    {
        var result = new List<DirectoryEntry>();
        if (depth > maxDepth)
        {
            return result;
        }

        List<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var child in children)
        {
            var entry = ToEntry(child);
            result.Add(entry);
            if (entry.Kind == PathKind.Directory && depth < maxDepth)
            {
                result.AddRange(EnumerateRecursive(child, maxDepth, depth + 1));
            }
        }

        return result;
    }

    private static IReadOnlyList<DirectoryEntry> SortEntries(IEnumerable<DirectoryEntry> entries)
    {
        var comparer = GetNameComparer();
        return entries
            .OrderBy(e => e.Kind == PathKind.Directory ? 0 : 1)
            .ThenBy(e => e.Name, comparer)
            .ToList()
            .AsReadOnly();
    }

    private static StringComparer GetNameComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool MatchSegments(string[] patternSegments, string[] pathSegments)
    {
        if (!patternSegments.Contains("**"))
        {
            // Spec 5.4: patterns are always recursive, so a pattern without
            // "**" matches the trailing path segments ("*.cs" hits any depth,
            // "sub/*.cs" hits any ".../sub/x.cs").
            if (patternSegments.Length > pathSegments.Length)
            {
                return false;
            }

            var offset = pathSegments.Length - patternSegments.Length;
            for (var i = 0; i < patternSegments.Length; i++)
            {
                if (!FileSystemName.MatchesWin32Expression(patternSegments[i], pathSegments[offset + i]))
                {
                    return false;
                }
            }

            return true;
        }

        // "**" crosses directory boundaries; other segments match one level
        // with standard * / ? wildcards against the entry name.
        var pi = 0;
        var si = 0;
        var starPi = -1;
        var starSi = -1;

        while (si < pathSegments.Length)
        {
            if (pi < patternSegments.Length && patternSegments[pi] == "**")
            {
                starPi = pi;
                starSi = si;
                pi++;
                continue;
            }

            if (pi < patternSegments.Length &&
                FileSystemName.MatchesWin32Expression(patternSegments[pi], pathSegments[si]))
            {
                pi++;
                si++;
                continue;
            }

            if (starPi != -1)
            {
                starSi++;
                si = starSi;
                pi = starPi + 1;
                continue;
            }

            return false;
        }

        while (pi < patternSegments.Length && patternSegments[pi] == "**")
        {
            pi++;
        }

        return pi == patternSegments.Length;
    }
}
