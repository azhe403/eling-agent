using System.IO.Enumeration;
using Eling.Core;
using Eling.Core.Exceptions;
using Eling.Core.FileSystem;

namespace Eling.Backend.Tests.FileSystem;

/// <summary>
/// In-memory <see cref="IFileSystemService"/> for wrapper tests. Mirrors
/// the <c>FakeMemoryService</c> pattern: no disk, same sandbox rule.
/// </summary>
internal sealed class FakeFileSystemService : IFileSystemService
{
    private sealed class FakeEntry
    {
        public PathKind Kind { get; set; }
        public string Content { get; set; } = "";
        public bool IsBinary { get; set; }
        public long SizeOverride { get; set; } = -1;
    }

    private readonly string _rootPath;
    private readonly string _rootWithSeparator;
    private readonly StringComparison _comparison;
    private readonly Dictionary<string, FakeEntry> _entries = new();

    public FakeFileSystemService(string rootPath = "/fake-root")
    {
        _rootPath = Normalize(rootPath);
        _rootWithSeparator = _rootPath.TrimEnd('/') + "/";
        _comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _entries[_rootPath] = new FakeEntry { Kind = PathKind.Directory };
    }

    public void AddDirectory(string relativePath)
        => _entries[Resolve(relativePath)] = new FakeEntry { Kind = PathKind.Directory };

    public void AddFile(string relativePath, string content)
        => _entries[Resolve(relativePath)] = new FakeEntry { Kind = PathKind.File, Content = content };

    public void AddBinaryFile(string relativePath)
        => _entries[Resolve(relativePath)] = new FakeEntry { Kind = PathKind.File, Content = "bin", IsBinary = true };

    public void AddOversizedFile(string relativePath, long sizeBytes)
        => _entries[Resolve(relativePath)] = new FakeEntry { Kind = PathKind.File, Content = "x", SizeOverride = sizeBytes };

    public PathInfo TestPath(string path)
    {
        var full = Resolve(path);
        if (_entries.TryGetValue(full, out var entry))
        {
            var size = entry.Kind == PathKind.File ? SizeOf(entry) : (long?)null;
            return new PathInfo(full, true, entry.Kind, size);
        }

        return new PathInfo(full, false, PathKind.NotFound, null);
    }

    public DirectoryCreateResult CreateDirectory(string path)
    {
        var full = Resolve(path);
        if (_entries.TryGetValue(full, out var existing))
        {
            if (existing.Kind != PathKind.Directory)
            {
                throw new NotADirectoryException(full);
            }

            return new DirectoryCreateResult(full, false);
        }

        EnsureParents(full);
        _entries[full] = new FakeEntry { Kind = PathKind.Directory };
        return new DirectoryCreateResult(full, true);
    }

    public IReadOnlyList<DirectoryEntry> ListDirectory(string path, bool recursive = false, int maxDepth = 3, string? pattern = null)
    {
        var full = Resolve(path);
        if (_entries.TryGetValue(full, out var existing) && existing.Kind == PathKind.File)
        {
            throw new NotADirectoryException(full);
        }

        if (!_entries.TryGetValue(full, out _) && !IsImplicitDirectory(full))
        {
            throw new DirectoryNotFoundException($"Directory '{full}' does not exist.");
        }

        var depth = recursive ? Math.Max(0, maxDepth) : 0;
        var result = _entries
            .Where(kv => IsDescendant(full, kv.Key, depth))
            .Select(kv => ToEntry(kv.Key, kv.Value))
            .ToList();

        if (!string.IsNullOrWhiteSpace(pattern))
        {
            result = result.Where(e => FileSystemName.MatchesWin32Expression(pattern, e.Name)).ToList();
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return result
            .OrderBy(e => e.Kind == PathKind.Directory ? 0 : 1)
            .ThenBy(e => e.Name, comparer)
            .ToList()
            .AsReadOnly();
    }

    public GlobResult Glob(string basePath, string pattern, int maxDepth = 5, int maxResults = 200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var baseFull = Resolve(basePath);
        if (_entries.TryGetValue(baseFull, out var existing) && existing.Kind == PathKind.File)
        {
            throw new NotADirectoryException(baseFull);
        }

        if (!_entries.TryGetValue(baseFull, out _) && !IsImplicitDirectory(baseFull))
        {
            throw new DirectoryNotFoundException($"Directory '{baseFull}' does not exist.");
        }

        var depth = Math.Max(0, maxDepth);
        var cap = Math.Max(1, maxResults);
        var segments = pattern.Split('/', '\\');
        var matches = _entries
            .Where(kv => IsDescendant(baseFull, kv.Key, depth))
            .Select(kv => ToEntry(kv.Key, kv.Value))
            .Where(e => MatchSuffix(segments, baseFull, e.Path))
            .Take(cap + 1)
            .OrderBy(e => e.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();

        var truncated = matches.Count > cap;
        var page = matches.Take(cap).ToList().AsReadOnly();
        return new GlobResult(baseFull, pattern, page.Count, truncated, page);
    }

    public FileReadResult ReadFile(string path, int maxBytes = 1048576, int offset = 1, int limit = 0)
    {
        var full = Resolve(path);
        if (!_entries.TryGetValue(full, out var entry))
        {
            throw new FileNotFoundException($"File '{full}' does not exist.", full);
        }

        if (entry.Kind != PathKind.File)
        {
            throw new NotAFileException(full);
        }

        if (entry.IsBinary)
        {
            throw new BinaryFileException(full);
        }

        var cap = maxBytes > 0 ? maxBytes : 1048576;
        var size = SizeOf(entry);
        if (size > cap)
        {
            throw new FileTooLargeException(full, size, cap);
        }

        var lines = SplitContentLines(entry.Content);
        if (offset <= 1 && limit <= 0)
        {
            return new FileReadResult(full, size, entry.Content, 1, lines.Count, lines.Count, false);
        }

        var start = Math.Max(offset, 1);
        var startIndex = start - 1;
        if (startIndex >= lines.Count)
        {
            return new FileReadResult(full, size, "", start, start - 1, lines.Count, false);
        }

        var take = limit <= 0 ? lines.Count - startIndex : Math.Min(limit, lines.Count - startIndex);
        var slice = string.Join('\n', lines.GetRange(startIndex, take));
        return new FileReadResult(full, size, slice, start, startIndex + take, lines.Count, startIndex + take < lines.Count);
    }

    private static List<string> SplitContentLines(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    public FileWriteResult WriteFile(string path, string content, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        var full = Resolve(path);
        if (_entries.TryGetValue(full, out var existing))
        {
            if (existing.Kind != PathKind.File)
            {
                throw new NotAFileException(full);
            }

            if (!overwrite)
            {
                throw new FileAlreadyExistsException(full);
            }

            existing.Content = content;
            existing.IsBinary = false;
            existing.SizeOverride = -1;
            return new FileWriteResult(full, SizeOf(existing), true);
        }

        EnsureParents(full);
        var entry = new FakeEntry { Kind = PathKind.File, Content = content };
        _entries[full] = entry;
        return new FileWriteResult(full, SizeOf(entry), false);
    }

    public DeleteResult DeleteFile(string path)
    {
        var full = Resolve(path);
        RejectRoot(full, "delete");
        if (!_entries.TryGetValue(full, out var entry))
        {
            throw new FileNotFoundException($"File '{full}' does not exist.", full);
        }

        if (entry.Kind != PathKind.File)
        {
            throw new NotAFileException(full);
        }

        _entries.Remove(full);
        return new DeleteResult(full, true);
    }

    public DeleteResult DeleteDirectory(string path, bool recursive = false)
    {
        var full = Resolve(path);
        RejectRoot(full, "delete");
        if (_entries.TryGetValue(full, out var entry) && entry.Kind == PathKind.File)
        {
            throw new NotADirectoryException(full);
        }

        var isKnownDir = _entries.TryGetValue(full, out _) || IsImplicitDirectory(full);
        if (!isKnownDir)
        {
            throw new DirectoryNotFoundException($"Directory '{full}' does not exist.");
        }

        var descendants = _entries.Keys.Where(k => IsDescendant(full, k, int.MaxValue)).ToList();
        if (!recursive && descendants.Count > 0)
        {
            throw new DirectoryNotEmptyException(full);
        }

        foreach (var key in descendants)
        {
            _entries.Remove(key);
        }

        _entries.Remove(full);
        return new DeleteResult(full, true);
    }

    public MoveResult MoveFile(string sourcePath, string destinationPath, bool overwrite = false)
    {
        var srcFull = Resolve(sourcePath);
        var dstFull = Resolve(destinationPath);
        RejectRoot(srcFull, "move");
        RejectRoot(dstFull, "move to");
        if (string.Equals(srcFull, dstFull, _comparison))
        {
            throw new ArgumentException("Source and destination are the same path.", nameof(destinationPath));
        }

        if (!_entries.TryGetValue(srcFull, out var src))
        {
            throw new FileNotFoundException($"File '{srcFull}' does not exist.", srcFull);
        }

        if (src.Kind != PathKind.File)
        {
            throw new NotAFileException(srcFull);
        }

        if (_entries.TryGetValue(dstFull, out var dst))
        {
            if (dst.Kind != PathKind.File)
            {
                throw new NotAFileException(dstFull);
            }

            if (!overwrite)
            {
                throw new FileAlreadyExistsException(dstFull);
            }
        }

        EnsureParents(dstFull);
        var overwrote = _entries.ContainsKey(dstFull);
        _entries[dstFull] = src;
        _entries.Remove(srcFull);
        return new MoveResult(srcFull, dstFull, overwrote);
    }

    public MoveResult MoveDirectory(string sourcePath, string destinationPath, bool overwrite = false)
    {
        var srcFull = Resolve(sourcePath);
        var dstFull = Resolve(destinationPath);
        RejectRoot(srcFull, "move");
        RejectRoot(dstFull, "move to");
        if (string.Equals(srcFull, dstFull, _comparison))
        {
            throw new ArgumentException("Source and destination are the same path.", nameof(destinationPath));
        }

        if (_entries.TryGetValue(srcFull, out var src) && src.Kind == PathKind.File)
        {
            throw new NotADirectoryException(srcFull);
        }

        if (!_entries.TryGetValue(srcFull, out _) && !IsImplicitDirectory(srcFull))
        {
            throw new DirectoryNotFoundException($"Directory '{srcFull}' does not exist.");
        }

        if (dstFull.StartsWith(srcFull + "/", _comparison))
        {
            throw new ArgumentException("Cannot move a directory inside itself.", nameof(destinationPath));
        }

        var overwrote = _entries.ContainsKey(dstFull) || IsImplicitDirectory(dstFull);
        if (overwrote && !overwrite)
        {
            throw new PathAlreadyExistsException(dstFull);
        }

        EnsureParents(dstFull);
        if (overwrote)
        {
            var doomed = _entries.Keys.Where(k => k == dstFull || k.StartsWith(dstFull + "/", _comparison)).ToList();
            foreach (var key in doomed)
            {
                _entries.Remove(key);
            }
        }

        var moved = _entries.Keys.Where(k => k == srcFull || k.StartsWith(srcFull + "/", _comparison)).ToList();
        foreach (var key in moved)
        {
            var relocated = dstFull + key[srcFull.Length..];
            _entries[relocated] = _entries[key];
            _entries.Remove(key);
        }

        _entries.TryAdd(dstFull, new FakeEntry { Kind = PathKind.Directory });
        return new MoveResult(srcFull, dstFull, overwrote);
    }

    public CopyResult CopyFile(string sourcePath, string destinationPath, bool overwrite = false)
    {
        var srcFull = Resolve(sourcePath);
        var dstFull = Resolve(destinationPath);
        RejectRoot(srcFull, "copy");
        RejectRoot(dstFull, "copy to");
        if (string.Equals(srcFull, dstFull, _comparison))
        {
            throw new ArgumentException("Source and destination are the same path.", nameof(destinationPath));
        }

        if (!_entries.TryGetValue(srcFull, out var src))
        {
            throw new FileNotFoundException($"File '{srcFull}' does not exist.", srcFull);
        }

        if (src.Kind != PathKind.File)
        {
            throw new NotAFileException(srcFull);
        }

        if (_entries.TryGetValue(dstFull, out var dst))
        {
            if (dst.Kind != PathKind.File)
            {
                throw new NotAFileException(dstFull);
            }

            if (!overwrite)
            {
                throw new FileAlreadyExistsException(dstFull);
            }
        }

        EnsureParents(dstFull);
        var overwrote = _entries.ContainsKey(dstFull);
        _entries[dstFull] = new FakeEntry
        {
            Kind = PathKind.File,
            Content = src.Content,
            IsBinary = src.IsBinary,
            SizeOverride = src.SizeOverride
        };
        return new CopyResult(srcFull, dstFull, overwrote, 1);
    }

    public CopyResult CopyDirectory(string sourcePath, string destinationPath, bool overwrite = false)
    {
        var srcFull = Resolve(sourcePath);
        var dstFull = Resolve(destinationPath);
        RejectRoot(srcFull, "copy");
        RejectRoot(dstFull, "copy to");
        if (string.Equals(srcFull, dstFull, _comparison))
        {
            throw new ArgumentException("Source and destination are the same path.", nameof(destinationPath));
        }

        if (_entries.TryGetValue(srcFull, out var src) && src.Kind == PathKind.File)
        {
            throw new NotADirectoryException(srcFull);
        }

        if (!_entries.TryGetValue(srcFull, out _) && !IsImplicitDirectory(srcFull))
        {
            throw new DirectoryNotFoundException($"Directory '{srcFull}' does not exist.");
        }

        if (dstFull.StartsWith(srcFull + "/", _comparison))
        {
            throw new ArgumentException("Cannot copy a directory inside itself.", nameof(destinationPath));
        }

        var overwrote = _entries.ContainsKey(dstFull) || IsImplicitDirectory(dstFull);
        if (overwrote && !overwrite)
        {
            throw new PathAlreadyExistsException(dstFull);
        }

        EnsureParents(dstFull);
        if (overwrote)
        {
            var doomed = _entries.Keys.Where(k => k == dstFull || k.StartsWith(dstFull + "/", _comparison)).ToList();
            foreach (var key in doomed)
            {
                _entries.Remove(key);
            }
        }

        _entries[dstFull] = new FakeEntry { Kind = PathKind.Directory };
        var copied = 0;
        var sources = _entries.Keys.Where(k => k.StartsWith(srcFull + "/", _comparison)).ToList();
        foreach (var key in sources)
        {
            var entry = _entries[key];
            _entries[dstFull + key[srcFull.Length..]] = new FakeEntry
            {
                Kind = entry.Kind,
                Content = entry.Content,
                IsBinary = entry.IsBinary,
                SizeOverride = entry.SizeOverride
            };
            if (entry.Kind == PathKind.File)
            {
                copied++;
            }
        }

        return new CopyResult(srcFull, dstFull, overwrote, copied);
    }

    private void RejectRoot(string full, string verb)
    {
        if (string.Equals(full, _rootPath, _comparison))
        {
            throw new ArgumentException($"Cannot {verb} the sandbox root itself.", nameof(full));
        }
    }

    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be empty.", nameof(path));
        }

        var combined = path.Replace('\\', '/');
        var full = combined.StartsWith('/')
            ? Normalize(combined)
            : Normalize(_rootPath + "/" + combined);
        if (!full.StartsWith(_rootWithSeparator, _comparison) &&
            !string.Equals(full, _rootPath, _comparison))
        {
            throw new PathSandboxException(full, _rootPath);
        }

        return full;
    }

    private static string Normalize(string path)
    {
        var parts = new Stack<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count > 0)
                {
                    parts.Pop();
                }

                continue;
            }

            parts.Push(segment);
        }

        return "/" + string.Join('/', parts.Reverse());
    }

    private void EnsureParents(string full)
    {
        var dir = ParentOf(full);
        while (dir is not null && !_entries.ContainsKey(dir))
        {
            _entries[dir] = new FakeEntry { Kind = PathKind.Directory };
            dir = ParentOf(dir);
        }
    }

    private static string? ParentOf(string full)
    {
        var index = full.LastIndexOf('/');
        return index <= 0 ? null : full[..index];
    }

    private bool IsImplicitDirectory(string full)
        => _entries.Keys.Any(k => k.StartsWith(full + "/", _comparison));

    private bool IsDescendant(string baseFull, string candidate, int depth)
    {
        if (!candidate.StartsWith(baseFull + "/", _comparison))
        {
            return false;
        }

        if (depth == int.MaxValue)
        {
            return true;
        }

        var rest = candidate[(baseFull.Length + 1)..];
        return rest.Split('/').Length <= depth + 1;
    }

    public FileEditResult EditFile(string path, string oldString, string newString, bool replaceAll = false, int maxBytes = 1048576)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldString);
        ArgumentNullException.ThrowIfNull(newString);
        var full = Resolve(path);
        if (!_entries.TryGetValue(full, out var entry))
        {
            throw new FileNotFoundException($"File '{full}' does not exist.", full);
        }

        if (entry.Kind != PathKind.File)
        {
            throw new NotAFileException(full);
        }

        if (entry.IsBinary)
        {
            throw new BinaryFileException(full);
        }

        var cap = maxBytes > 0 ? maxBytes : 1048576;
        if (SizeOf(entry) > cap)
        {
            throw new FileTooLargeException(full, SizeOf(entry), cap);
        }

        var needle = oldString.Replace("\r\n", "\n");
        var content = entry.Content.Replace("\r\n", "\n");
        var count = CountOccurrences(content, needle);
        if (count == 0)
        {
            throw new OldStringNotFoundException(full);
        }

        if (count > 1 && !replaceAll)
        {
            throw new AmbiguousMatchException(full, count);
        }

        entry.Content = replaceAll
            ? content.Replace(needle, newString.Replace("\r\n", "\n"))
            : ReplaceFirst(content, needle, newString.Replace("\r\n", "\n"));
        var replacements = replaceAll ? count : 1;
        return new FileEditResult(full, SizeOf(entry), replacements);
    }

    public FileAppendResult AppendFile(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var full = Resolve(path);
        if (full == _rootPath)
        {
            throw new NotAFileException(full);
        }

        if (_entries.TryGetValue(full, out var entry))
        {
            if (entry.Kind != PathKind.File)
            {
                throw new NotAFileException(full);
            }

            if (entry.IsBinary)
            {
                throw new BinaryFileException(full);
            }

            entry.Content += content;
            return new FileAppendResult(full, SizeOf(entry), false);
        }

        EnsureParents(full);
        _entries[full] = new FakeEntry { Kind = PathKind.File, Content = content };
        return new FileAppendResult(full, SizeOf(_entries[full]), true);
    }

    public ContentSearchResult SearchFiles(string basePath, string pattern, bool useRegex = false, bool caseSensitive = false, string? filePattern = null, int maxDepth = 5, int maxResults = 100, int maxFileBytes = 1048576)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var baseFull = Resolve(basePath);
        if (_entries.TryGetValue(baseFull, out var existing) && existing.Kind == PathKind.File)
        {
            throw new NotADirectoryException(baseFull);
        }

        if (!_entries.TryGetValue(baseFull, out _) && !IsImplicitDirectory(baseFull))
        {
            throw new DirectoryNotFoundException($"Directory '{baseFull}' does not exist.");
        }

        System.Text.RegularExpressions.Regex? regex = null;
        if (useRegex)
        {
            try
            {
                regex = new System.Text.RegularExpressions.Regex(
                    pattern,
                    caseSensitive
                        ? System.Text.RegularExpressions.RegexOptions.None
                        : System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid search pattern: {ex.Message}", nameof(pattern), ex);
            }
        }

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var depth = Math.Max(0, maxDepth);
        var cap = Math.Max(1, maxResults);
        var fileCap = maxFileBytes > 0 ? maxFileBytes : 1048576;
        var matches = new List<ContentMatch>();

        foreach (var kv in _entries.Where(kv => kv.Value.Kind == PathKind.File && IsDescendant(baseFull, kv.Key, depth)))
        {
            var name = kv.Key.Contains('/') ? kv.Key[(kv.Key.LastIndexOf('/') + 1)..] : kv.Key;
            if (!string.IsNullOrWhiteSpace(filePattern) &&
                !FileSystemName.MatchesWin32Expression(filePattern, name))
            {
                continue;
            }

            if (kv.Value.IsBinary || SizeOf(kv.Value) > fileCap)
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in kv.Value.Content.Replace("\r\n", "\n").Split('\n'))
            {
                lineNumber++;
                var hit = regex is not null ? regex.IsMatch(line) : line.Contains(pattern, comparison);
                if (!hit)
                {
                    continue;
                }

                matches.Add(new ContentMatch(kv.Key, lineNumber, line.Length > 500 ? line[..500] : line));
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
            .OrderBy(m => m.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ThenBy(m => m.LineNumber)
            .ToList()
            .AsReadOnly();
        return new ContentSearchResult(baseFull, pattern, page.Count, truncated, page);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string needle, string replacement)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        return text[..index] + replacement + text[(index + needle.Length)..];
    }

    private static DirectoryEntry ToEntry(string full, FakeEntry entry)
    {
        var name = full.Contains('/') ? full[(full.LastIndexOf('/') + 1)..] : full;
        var size = entry.Kind == PathKind.File ? SizeOf(entry) : (long?)null;
        return new DirectoryEntry(full, name, entry.Kind, size);
    }

    private static bool MatchSuffix(string[] patternSegments, string baseFull, string candidatePath)
    {
        var effective = patternSegments.Where(s => s != "**").ToArray();
        var relative = candidatePath.StartsWith(baseFull + "/", StringComparison.Ordinal)
            ? candidatePath[(baseFull.Length + 1)..]
            : candidatePath;
        var pathSegments = relative.Split('/');
        if (effective.Length > pathSegments.Length)
        {
            return false;
        }

        var offset = pathSegments.Length - effective.Length;
        for (var i = 0; i < effective.Length; i++)
        {
            if (!FileSystemName.MatchesWin32Expression(effective[i], pathSegments[offset + i]))
            {
                return false;
            }
        }

        return true;
    }

    private static long SizeOf(FakeEntry entry)
        => entry.SizeOverride >= 0 ? entry.SizeOverride : entry.Content.Length;
}
