using Eling.Backend.FileSystem;
using Eling.Core;
using Eling.Core.Exceptions;
using Eling.Core.FileSystem;

namespace Eling.Backend.Tests.FileSystem;

/// <summary>
/// Sandbox and IO tests for <see cref="FileSystemService"/> against a
/// per-test temp directory. The directory is removed on dispose.
/// </summary>
public sealed class FileSystemServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemService _service;

    public FileSystemServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-fs-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
        _service = new FileSystemService(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a leftover temp dir must not fail the suite.
        }
    }

    [Fact]
    public void ResolvePath_RelativeInput_LandsUnderRoot()
    {
        var info = _service.TestPath("sub/file.txt");

        Assert.StartsWith(_root, info.ResolvedPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(info.Exists);
        Assert.Equal(PathKind.NotFound, info.Kind);
    }

    [Fact]
    public void ResolvePath_AbsoluteInputUnderRoot_Accepted()
    {
        var absolute = Path.Combine(_root, "a.txt");

        var info = _service.TestPath(absolute);

        Assert.Equal(absolute, info.ResolvedPath);
    }

    [Fact]
    public void ResolvePath_AbsoluteInputOutsideRoot_ThrowsPathSandboxException()
    {
        var outside = Path.Combine(Path.GetTempPath(), "eling-outside-" + Guid.NewGuid().ToString("N")[..8]);

        Assert.Throws<PathSandboxException>(() => _service.TestPath(outside));
    }

    [Fact]
    public void ResolvePath_ParentTraversal_ThrowsPathSandboxException()
    {
        Assert.Throws<PathSandboxException>(() => _service.TestPath("../escaping.txt"));
        Assert.Throws<PathSandboxException>(() => _service.TestPath("sub/../../escaping.txt"));
    }

    [Fact]
    public void ResolvePath_SiblingPrefixConfusion_Rejected()
    {
        // "<root>Other" shares a string prefix with root but is not under it.
        var sibling = _root + "Other";

        Assert.Throws<PathSandboxException>(() => _service.TestPath(sibling));
    }

    [Fact]
    public void WriteThenRead_RoundTripsContent()
    {
        var written = _service.WriteFile("docs/note.txt", "eling bang");

        Assert.True(written.SizeBytes > 0);
        Assert.False(written.Overwrote);

        var read = _service.ReadFile("docs/note.txt");
        Assert.Equal("eling bang", read.Content);

        var info = _service.TestPath("docs/note.txt");
        Assert.True(info.Exists);
        Assert.Equal(PathKind.File, info.Kind);
    }

    [Fact]
    public void ReadFile_BinaryContent_ThrowsBinaryFileException()
    {
        var target = Path.Combine(_root, "blob.bin");
        File.WriteAllBytes(target, [0x48, 0x00, 0x49]);

        Assert.Throws<BinaryFileException>(() => _service.ReadFile("blob.bin"));
    }

    [Fact]
    public void ReadFile_OversizedContent_ThrowsFileTooLargeException()
    {
        var target = Path.Combine(_root, "big.txt");
        File.WriteAllText(target, new string('x', 64));

        Assert.Throws<FileTooLargeException>(() => _service.ReadFile("big.txt", maxBytes: 16));
    }

    [Fact]
    public void ReadFile_MissingFile_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() => _service.ReadFile("nope.txt"));
    }

    [Fact]
    public void CreateDirectory_ExistingDirectory_ReturnsCreatedFalse()
    {
        var first = _service.CreateDirectory("d");
        var second = _service.CreateDirectory("d");

        Assert.True(first.Created);
        Assert.False(second.Created);
    }

    [Fact]
    public void Glob_StarPattern_FindsTopLevelFiles()
    {
        _service.WriteFile("a.cs", "1", overwrite: true);
        _service.WriteFile("sub/b.cs", "2", overwrite: true);

        var result = _service.Glob(".", "*.cs");

        Assert.Equal(2, result.MatchCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void DeleteFile_RemovesFileFromDisk()
    {
        _service.WriteFile("gone.txt", "x", overwrite: true);

        var result = _service.DeleteFile("gone.txt");

        Assert.True(result.Deleted);
        Assert.False(File.Exists(Path.Combine(_root, "gone.txt")));
    }

    [Fact]
    public void DeleteFile_MissingFile_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() => _service.DeleteFile("nope.txt"));
    }

    [Fact]
    public void DeleteDirectory_NonEmptyWithoutRecursive_ThrowsDirectoryNotEmptyException()
    {
        _service.WriteFile("dir/f.txt", "x", overwrite: true);

        Assert.Throws<DirectoryNotEmptyException>(() => _service.DeleteDirectory("dir"));
        Assert.True(File.Exists(Path.Combine(_root, "dir", "f.txt")));
    }

    [Fact]
    public void DeleteDirectory_Recursive_RemovesTree()
    {
        _service.WriteFile("dir/sub/f.txt", "x", overwrite: true);

        var result = _service.DeleteDirectory("dir", recursive: true);

        Assert.True(result.Deleted);
        Assert.False(Directory.Exists(Path.Combine(_root, "dir")));
    }

    [Fact]
    public void Delete_SandboxRoot_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _service.DeleteFile("."));
        Assert.Throws<ArgumentException>(() => _service.DeleteDirectory("."));
    }

    [Fact]
    public void MoveFile_RelocatesOnDisk()
    {
        _service.WriteFile("a.txt", "data", overwrite: true);

        var result = _service.MoveFile("a.txt", "sub/b.txt");

        Assert.False(result.Overwrote);
        Assert.False(File.Exists(Path.Combine(_root, "a.txt")));
        Assert.Equal("data", File.ReadAllText(Path.Combine(_root, "sub", "b.txt")));
    }

    [Fact]
    public void MoveFile_ExistingDestinationNoOverwrite_ThrowsAlreadyExists()
    {
        _service.WriteFile("a.txt", "new", overwrite: true);
        _service.WriteFile("b.txt", "original", overwrite: true);

        Assert.ThrowsAny<PathAlreadyExistsException>(() => _service.MoveFile("a.txt", "b.txt"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(_root, "b.txt")));
    }

    [Fact]
    public void MoveDirectory_RelocatesTreeOnDisk()
    {
        _service.WriteFile("src/sub/f.txt", "x", overwrite: true);

        var result = _service.MoveDirectory("src", "dst");

        Assert.False(result.Overwrote);
        Assert.False(Directory.Exists(Path.Combine(_root, "src")));
        Assert.Equal("x", File.ReadAllText(Path.Combine(_root, "dst", "sub", "f.txt")));
    }

    [Fact]
    public void MoveDirectory_IntoItself_ThrowsArgumentException()
    {
        _service.CreateDirectory("src");

        Assert.Throws<ArgumentException>(() => _service.MoveDirectory("src", "src/inner"));
    }

    [Fact]
    public void EditFile_UniqueMatch_ReplacesOnDisk()
    {
        _service.WriteFile("code.txt", "a\nold\nb", overwrite: true);

        var result = _service.EditFile("code.txt", "old", "new");

        Assert.Equal(1, result.Replacements);
        Assert.Equal("a\nnew\nb", File.ReadAllText(Path.Combine(_root, "code.txt")));
    }

    [Fact]
    public void EditFile_CrlfFileWithLfOldString_Replaces()
    {
        File.WriteAllText(Path.Combine(_root, "crlf.txt"), "a\r\nold\r\nb");

        var result = _service.EditFile("crlf.txt", "old", "new");

        Assert.Equal(1, result.Replacements);
        Assert.Equal("a\r\nnew\r\nb", File.ReadAllText(Path.Combine(_root, "crlf.txt")));
    }

    [Fact]
    public void EditFile_MissingOldString_ThrowsOldStringNotFoundException()
    {
        _service.WriteFile("code.txt", "content", overwrite: true);

        Assert.Throws<OldStringNotFoundException>(() => _service.EditFile("code.txt", "absent", "x"));
    }

    [Fact]
    public void EditFile_AmbiguousWithoutReplaceAll_ThrowsAmbiguousMatchException()
    {
        _service.WriteFile("code.txt", "dup\nx\ndup", overwrite: true);

        var ex = Assert.Throws<AmbiguousMatchException>(() => _service.EditFile("code.txt", "dup", "y"));
        Assert.Equal(2, ex.MatchCount);
    }

    [Fact]
    public void AppendFile_CreatesThenAppends()
    {
        var created = _service.AppendFile("log.txt", "one\n");
        var appended = _service.AppendFile("log.txt", "two\n");

        Assert.True(created.Created);
        Assert.False(appended.Created);
        Assert.Equal("one\ntwo\n", File.ReadAllText(Path.Combine(_root, "log.txt")));
    }

    [Fact]
    public void SearchFiles_LiteralFindsHitsAndSkipsBinary()
    {
        _service.WriteFile("a.cs", "needle here\nnothing", overwrite: true);
        _service.WriteFile("sub/b.cs", "NEEDLE again", overwrite: true);
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [0x6E, 0x00, 0x65]);

        var result = _service.SearchFiles(".", "needle");

        Assert.Equal(2, result.MatchCount);
        Assert.False(result.Truncated);
        Assert.All(result.Matches, m => Assert.Equal(1, m.LineNumber));
    }

    [Fact]
    public void SearchFiles_InvalidRegex_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _service.SearchFiles(".", "([", useRegex: true));
    }

    [Fact]
    public void ReadFile_PaginatedSlice_ReturnsLinesAndCounts()
    {
        _service.WriteFile("p.txt", "l1\nl2\nl3\nl4", overwrite: true);

        var result = _service.ReadFile("p.txt", offset: 2, limit: 2);

        Assert.Equal("l2\nl3", result.Content);
        Assert.Equal(2, result.StartLine);
        Assert.Equal(3, result.EndLine);
        Assert.Equal(4, result.TotalLines);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void ReadFile_CrlfSlice_NormalizesToLf()
    {
        File.WriteAllText(Path.Combine(_root, "crlf.txt"), "a\r\nb\r\nc\r\n");

        var result = _service.ReadFile("crlf.txt", offset: 2, limit: 1);

        Assert.Equal("b", result.Content);
        Assert.Equal(3, result.TotalLines);
    }

    [Fact]
    public void CopyFile_CopiesOnDiskLeavingSource()
    {
        _service.WriteFile("a.txt", "data", overwrite: true);

        var result = _service.CopyFile("a.txt", "sub/b.txt");

        Assert.False(result.Overwrote);
        Assert.Equal(1, result.EntriesCopied);
        Assert.Equal("data", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal("data", File.ReadAllText(Path.Combine(_root, "sub", "b.txt")));
    }

    [Fact]
    public void CopyFile_ExistingDestinationNoOverwrite_ThrowsAlreadyExists()
    {
        _service.WriteFile("a.txt", "new", overwrite: true);
        _service.WriteFile("b.txt", "original", overwrite: true);

        Assert.ThrowsAny<PathAlreadyExistsException>(() => _service.CopyFile("a.txt", "b.txt"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(_root, "b.txt")));
    }

    [Fact]
    public void CopyDirectory_CopiesTreeOnDisk()
    {
        _service.WriteFile("src/a.txt", "1", overwrite: true);
        _service.WriteFile("src/sub/b.txt", "2", overwrite: true);

        var result = _service.CopyDirectory("src", "dst");

        Assert.Equal(2, result.EntriesCopied);
        Assert.False(result.Overwrote);
        Assert.Equal("1", File.ReadAllText(Path.Combine(_root, "dst", "a.txt")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(_root, "dst", "sub", "b.txt")));
        Assert.Equal("1", File.ReadAllText(Path.Combine(_root, "src", "a.txt")));
    }

    [Fact]
    public void CopyDirectory_IntoItself_ThrowsArgumentException()
    {
        _service.CreateDirectory("src");

        Assert.Throws<ArgumentException>(() => _service.CopyDirectory("src", "src/inner"));
    }

    [Fact]
    public void Copy_SandboxRoot_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _service.CopyDirectory(".", "copy-of-root"));
    }

    [Fact]
    public void ListDirectory_NegativeMaxDepth_UsesServiceDefault()
    {
        var service = new FileSystemService(_root, defaultMaxDepth: 1);
        service.WriteFile("a/f1.txt", "x", overwrite: true);
        service.WriteFile("a/b/f2.txt", "x", overwrite: true);

        var names = service.ListDirectory(".", recursive: true, maxDepth: -1)
            .Select(e => e.Name)
            .ToList();

        Assert.Contains("f1.txt", names);
        Assert.DoesNotContain("f2.txt", names);
    }

    [Fact]
    public void DeleteDirectory_DirectorySymlink_RemovesLinkAndKeepsTarget()
    {
        _service.CreateDirectory("target");
        _service.WriteFile("target/f.txt", "x", overwrite: true);
        CreateDirectoryLink(Path.Combine(_root, "link"), Path.Combine(_root, "target"));

        var result = _service.DeleteDirectory("link");

        Assert.True(result.Deleted);
        Assert.False(Directory.Exists(Path.Combine(_root, "link")));
        Assert.True(File.Exists(Path.Combine(_root, "target", "f.txt")));
    }

    [Fact]
    public void DeleteFile_OnDirectorySymlink_ThrowsNotAFile()
    {
        _service.CreateDirectory("target");
        CreateDirectoryLink(Path.Combine(_root, "link"), Path.Combine(_root, "target"));

        Assert.Throws<NotAFileException>(() => _service.DeleteFile("link"));
        Assert.True(Directory.Exists(Path.Combine(_root, "link")));
    }

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException(
                "These symlink tests need to create a symbolic link. On Windows that requires Developer Mode " +
                "(Settings > System > For developers > Developer Mode) or an elevated process. " +
                "Enable Developer Mode, then re-run the tests.",
                ex);
        }
    }
}
