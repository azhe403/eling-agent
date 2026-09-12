using System.Text.Json;
using Eling.Backend.Mcp.Tools;

namespace Eling.Backend.Tests.FileSystem;

/// <summary>
/// Wrapper tests for the six filesystem MCP tools, driven by
/// <see cref="FakeFileSystemService"/> so no disk is touched.
/// </summary>
public class FileSystemToolsTests
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static FileSystemTools CreateTool(FakeFileSystemService? service = null)
        => new(service ?? new FakeFileSystemService());

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    private static string CodeOf(string errorJson)
        => Parse(errorJson).RootElement.GetProperty("code").GetString()!;

    // ---------- path_test ----------

    [Fact]
    public void PathTest_ExistingFile_ReturnsFileInfo()
    {
        var service = new FakeFileSystemService();
        service.AddFile("notes.txt", "hello");
        var tool = CreateTool(service);

        var root = Parse(tool.PathTest("notes.txt")).RootElement;
        Assert.True(root.GetProperty("exists").GetBoolean());
        Assert.Equal("file", root.GetProperty("kind").GetString());
        Assert.Equal(5, root.GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public void PathTest_NonExistent_ReturnsNotFoundShape()
    {
        var tool = CreateTool();

        var root = Parse(tool.PathTest("missing.txt")).RootElement;
        Assert.False(root.GetProperty("exists").GetBoolean());
        Assert.Equal("notFound", root.GetProperty("kind").GetString());
    }

    [Fact]
    public void PathTest_OutsideRoot_ReturnsSandboxError()
    {
        var tool = CreateTool();

        Assert.Equal("sandbox_violation", CodeOf(tool.PathTest("../outside.txt")));
        Assert.Equal("sandbox_violation", CodeOf(tool.PathTest("/etc/passwd")));
    }

    [Fact]
    public void PathTest_EmptyPath_ReturnsInvalidArgument()
    {
        var tool = CreateTool();

        Assert.Equal("invalid_argument", CodeOf(tool.PathTest("   ")));
    }

    // ---------- directory_create ----------

    [Fact]
    public void DirectoryCreate_NewDirectory_ReturnsCreatedTrue()
    {
        var tool = CreateTool();

        var root = Parse(tool.CreateDirectory("a/b")).RootElement;
        Assert.True(root.GetProperty("created").GetBoolean());
        Assert.True(Parse(tool.PathTest("a/b")).RootElement.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public void DirectoryCreate_ExistingDirectory_ReturnsCreatedFalse()
    {
        var service = new FakeFileSystemService();
        service.AddDirectory("docs");
        var tool = CreateTool(service);

        Assert.False(Parse(tool.CreateDirectory("docs")).RootElement.GetProperty("created").GetBoolean());
    }

    [Fact]
    public void DirectoryCreate_OutsideRoot_ReturnsSandboxError()
    {
        var tool = CreateTool();

        Assert.Equal("sandbox_violation", CodeOf(tool.CreateDirectory("../../evil")));
    }

    // ---------- directory_list ----------

    [Fact]
    public void DirectoryList_Flat_ReturnsDirectChildrenOnly()
    {
        var service = new FakeFileSystemService();
        service.AddFile("root.txt", "r");
        service.AddDirectory("sub");
        service.AddFile("sub/nested.txt", "n");
        var tool = CreateTool(service);

        var names = Parse(tool.ListDirectory(".")).RootElement
            .EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("root.txt", names);
        Assert.Contains("sub", names);
        Assert.DoesNotContain("nested.txt", names);
    }

    [Fact]
    public void DirectoryList_Recursive_ReturnsNestedEntries()
    {
        var service = new FakeFileSystemService();
        service.AddFile("sub/nested.txt", "n");
        var tool = CreateTool(service);

        var names = Parse(tool.ListDirectory(".", recursive: true)).RootElement
            .EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("nested.txt", names);
    }

    [Fact]
    public void DirectoryList_PatternFilter_ReturnsMatchesOnly()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.cs", "1");
        service.AddFile("b.md", "2");
        var tool = CreateTool(service);

        var names = Parse(tool.ListDirectory(".", pattern: "*.cs")).RootElement
            .EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Equal(["a.cs"], names);
    }

    [Fact]
    public void DirectoryList_OnFile_ReturnsNotADirectory()
    {
        var service = new FakeFileSystemService();
        service.AddFile("f.txt", "x");
        var tool = CreateTool(service);

        Assert.Equal("not_a_directory", CodeOf(tool.ListDirectory("f.txt")));
    }

    // ---------- glob ----------

    [Fact]
    public void Glob_DoubleStarPattern_ReturnsAllDescendants()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.cs", "1");
        service.AddFile("sub/b.cs", "2");
        service.AddFile("sub/c.md", "3");
        var tool = CreateTool(service);

        var root = Parse(tool.Glob(".", "**/*.cs")).RootElement;
        Assert.Equal(2, root.GetProperty("matchCount").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Glob_TruncationFlag_SetWhenMaxResultsExceeded()
    {
        var service = new FakeFileSystemService();
        for (var i = 0; i < 5; i++)
        {
            service.AddFile($"f{i}.txt", "x");
        }

        var tool = CreateTool(service);
        var root = Parse(tool.Glob(".", "*.txt", maxResults: 2)).RootElement;
        Assert.Equal(2, root.GetProperty("matchCount").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Glob_OutsideRoot_ReturnsSandboxError()
    {
        var tool = CreateTool();

        Assert.Equal("sandbox_violation", CodeOf(tool.Glob("..", "*.cs")));
    }

    // ---------- file_read ----------

    [Fact]
    public void FileRead_ExistingTextFile_ReturnsContent()
    {
        var service = new FakeFileSystemService();
        service.AddFile("hello.txt", "hi bang");
        var tool = CreateTool(service);

        Assert.Equal("hi bang", tool.ReadFile("hello.txt"));
    }

    [Fact]
    public void FileRead_BinaryFile_ReturnsBinaryError()
    {
        var service = new FakeFileSystemService();
        service.AddBinaryFile("blob.bin");
        var tool = CreateTool(service);

        Assert.Equal("binary_file", CodeOf(tool.ReadFile("blob.bin")));
    }

    [Fact]
    public void FileRead_OversizedFile_ReturnsFileTooLargeError()
    {
        var service = new FakeFileSystemService();
        service.AddOversizedFile("big.txt", 2_000_000);
        var tool = CreateTool(service);

        Assert.Equal("file_too_large", CodeOf(tool.ReadFile("big.txt")));
    }

    [Fact]
    public void FileRead_MissingFile_ReturnsNotFound()
    {
        var tool = CreateTool();

        Assert.Equal("not_found", CodeOf(tool.ReadFile("nope.txt")));
    }

    [Fact]
    public void FileRead_WholeFile_StillReturnsPlainString()
    {
        var service = new FakeFileSystemService();
        service.AddFile("w.txt", "a\nb\nc");
        var tool = CreateTool(service);

        Assert.Equal("a\nb\nc", tool.ReadFile("w.txt"));
    }

    [Fact]
    public void FileRead_Paginated_ReturnsEnvelopeWithLineNumbers()
    {
        var service = new FakeFileSystemService();
        service.AddFile("p.txt", "l1\nl2\nl3\nl4\nl5");
        var tool = CreateTool(service);

        var root = Parse(tool.ReadFile("p.txt", offset: 2, limit: 2)).RootElement;
        Assert.Equal("l2\nl3", root.GetProperty("content").GetString());
        Assert.Equal(2, root.GetProperty("startLine").GetInt32());
        Assert.Equal(3, root.GetProperty("endLine").GetInt32());
        Assert.Equal(5, root.GetProperty("totalLines").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void FileRead_OffsetBeyondEnd_ReturnsEmptyUntruncated()
    {
        var service = new FakeFileSystemService();
        service.AddFile("p.txt", "l1\nl2");
        var tool = CreateTool(service);

        var root = Parse(tool.ReadFile("p.txt", offset: 9)).RootElement;
        Assert.Equal("", root.GetProperty("content").GetString());
        Assert.Equal(2, root.GetProperty("totalLines").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
    }

    // ---------- file_write ----------

    [Fact]
    public void FileWrite_NewFile_CreatesParentAndFile()
    {
        var tool = CreateTool();

        var root = Parse(tool.WriteFile("new/dir/n.txt", "data")).RootElement;
        Assert.False(root.GetProperty("overwrote").GetBoolean());
        Assert.Equal("data", tool.ReadFile("new/dir/n.txt"));
    }

    [Fact]
    public void FileWrite_ExistingFileNoOverwrite_ReturnsAlreadyExistsError()
    {
        var service = new FakeFileSystemService();
        service.AddFile("keep.txt", "original");
        var tool = CreateTool(service);

        Assert.Equal("already_exists", CodeOf(tool.WriteFile("keep.txt", "changed")));
        Assert.Equal("original", tool.ReadFile("keep.txt"));
    }

    [Fact]
    public void FileWrite_ExistingFileOverwrite_ReplacesContent()
    {
        var service = new FakeFileSystemService();
        service.AddFile("keep.txt", "original");
        var tool = CreateTool(service);

        var root = Parse(tool.WriteFile("keep.txt", "changed", overwrite: true)).RootElement;
        Assert.True(root.GetProperty("overwrote").GetBoolean());
        Assert.Equal("changed", tool.ReadFile("keep.txt"));
    }

    [Fact]
    public void FileWrite_SerializesWithCamelCase()
    {
        var tool = CreateTool();

        var json = tool.WriteFile("c.txt", "v");
        Assert.Contains("\"resolvedPath\"", json);
        Assert.Contains("\"sizeBytes\"", json);
        Assert.DoesNotContain("ResolvedPath", json);
    }

    // ---------- file_delete ----------

    [Fact]
    public void FileDelete_ExistingFile_DeletesAndReports()
    {
        var service = new FakeFileSystemService();
        service.AddFile("gone.txt", "x");
        var tool = CreateTool(service);

        Assert.True(Parse(tool.DeleteFile("gone.txt")).RootElement.GetProperty("deleted").GetBoolean());
        Assert.False(Parse(tool.PathTest("gone.txt")).RootElement.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public void FileDelete_MissingFile_ReturnsNotFound()
    {
        var tool = CreateTool();

        Assert.Equal("not_found", CodeOf(tool.DeleteFile("nope.txt")));
    }

    [Fact]
    public void FileDelete_OnDirectory_ReturnsNotAFile()
    {
        var service = new FakeFileSystemService();
        service.AddDirectory("dir");
        var tool = CreateTool(service);

        Assert.Equal("not_a_file", CodeOf(tool.DeleteFile("dir")));
    }

    [Fact]
    public void FileDelete_Root_ReturnsInvalidArgument()
    {
        var tool = CreateTool();

        Assert.Equal("invalid_argument", CodeOf(tool.DeleteFile(".")));
    }

    // ---------- directory_delete ----------

    [Fact]
    public void DirectoryDelete_EmptyDirectory_ReturnsDeleted()
    {
        var service = new FakeFileSystemService();
        service.AddDirectory("empty");
        var tool = CreateTool(service);

        Assert.True(Parse(tool.DeleteDirectory("empty")).RootElement.GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public void DirectoryDelete_NonEmptyWithoutRecursive_ReturnsDirectoryNotEmpty()
    {
        var service = new FakeFileSystemService();
        service.AddFile("dir/f.txt", "x");
        var tool = CreateTool(service);

        Assert.Equal("directory_not_empty", CodeOf(tool.DeleteDirectory("dir")));
        Assert.True(Parse(tool.PathTest("dir/f.txt")).RootElement.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public void DirectoryDelete_Recursive_DeletesTree()
    {
        var service = new FakeFileSystemService();
        service.AddFile("dir/sub/f.txt", "x");
        var tool = CreateTool(service);

        Assert.True(Parse(tool.DeleteDirectory("dir", recursive: true)).RootElement.GetProperty("deleted").GetBoolean());
        Assert.False(Parse(tool.PathTest("dir/sub/f.txt")).RootElement.GetProperty("exists").GetBoolean());
    }

    // ---------- file_move ----------

    [Fact]
    public void FileMove_Basic_RelocatesAndReports()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.txt", "data");
        var tool = CreateTool(service);

        var root = Parse(tool.MoveFile("a.txt", "sub/b.txt")).RootElement;
        Assert.False(root.GetProperty("overwrote").GetBoolean());
        Assert.False(Parse(tool.PathTest("a.txt")).RootElement.GetProperty("exists").GetBoolean());
        Assert.Equal("data", tool.ReadFile("sub/b.txt"));
    }

    [Fact]
    public void FileMove_ExistingDestinationNoOverwrite_ReturnsAlreadyExists()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.txt", "new");
        service.AddFile("b.txt", "original");
        var tool = CreateTool(service);

        Assert.Equal("already_exists", CodeOf(tool.MoveFile("a.txt", "b.txt")));
        Assert.Equal("original", tool.ReadFile("b.txt"));
    }

    [Fact]
    public void FileMove_Overwrite_ReplacesDestination()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.txt", "new");
        service.AddFile("b.txt", "original");
        var tool = CreateTool(service);

        Assert.True(Parse(tool.MoveFile("a.txt", "b.txt", overwrite: true)).RootElement.GetProperty("overwrote").GetBoolean());
        Assert.Equal("new", tool.ReadFile("b.txt"));
    }

    // ---------- directory_move ----------

    [Fact]
    public void DirectoryMove_Basic_RelocatesTree()
    {
        var service = new FakeFileSystemService();
        service.AddFile("src/sub/f.txt", "x");
        var tool = CreateTool(service);

        var root = Parse(tool.MoveDirectory("src", "dst")).RootElement;
        Assert.False(root.GetProperty("overwrote").GetBoolean());
        Assert.False(Parse(tool.PathTest("src/sub/f.txt")).RootElement.GetProperty("exists").GetBoolean());
        Assert.Equal("x", tool.ReadFile("dst/sub/f.txt"));
    }

    [Fact]
    public void DirectoryMove_IntoItself_ReturnsInvalidArgument()
    {
        var service = new FakeFileSystemService();
        service.AddDirectory("src");
        var tool = CreateTool(service);

        Assert.Equal("invalid_argument", CodeOf(tool.MoveDirectory("src", "src/inner")));
    }

    [Fact]
    public void Move_DestinationOutsideRoot_ReturnsSandboxError()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.txt", "x");
        var tool = CreateTool(service);

        Assert.Equal("sandbox_violation", CodeOf(tool.MoveFile("a.txt", "../out.txt")));
    }

    // ---------- file_edit ----------

    [Fact]
    public void FileEdit_UniqueMatch_ReplacesAndReports()
    {
        var service = new FakeFileSystemService();
        service.AddFile("code.txt", "line1\nold value\nline3");
        var tool = CreateTool(service);

        var root = Parse(tool.EditFile("code.txt", "old value", "new value")).RootElement;
        Assert.Equal(1, root.GetProperty("replacements").GetInt32());
        Assert.Equal("line1\nnew value\nline3", tool.ReadFile("code.txt"));
    }

    [Fact]
    public void FileEdit_NotFound_ReturnsOldStringNotFound()
    {
        var service = new FakeFileSystemService();
        service.AddFile("code.txt", "nothing here");
        var tool = CreateTool(service);

        Assert.Equal("old_string_not_found", CodeOf(tool.EditFile("code.txt", "missing", "x")));
    }

    [Fact]
    public void FileEdit_AmbiguousWithoutReplaceAll_ReturnsMultipleMatches()
    {
        var service = new FakeFileSystemService();
        service.AddFile("code.txt", "dup\nmiddle\ndup");
        var tool = CreateTool(service);

        Assert.Equal("multiple_matches", CodeOf(tool.EditFile("code.txt", "dup", "x")));
        Assert.Equal("dup\nmiddle\ndup", tool.ReadFile("code.txt"));
    }

    [Fact]
    public void FileEdit_ReplaceAll_ReplacesEverywhere()
    {
        var service = new FakeFileSystemService();
        service.AddFile("code.txt", "dup\nmiddle\ndup");
        var tool = CreateTool(service);

        var root = Parse(tool.EditFile("code.txt", "dup", "x", replaceAll: true)).RootElement;
        Assert.Equal(2, root.GetProperty("replacements").GetInt32());
        Assert.Equal("x\nmiddle\nx", tool.ReadFile("code.txt"));
    }

    // ---------- file_append ----------

    [Fact]
    public void FileAppend_NewFile_CreatesWithContent()
    {
        var tool = CreateTool();

        var root = Parse(tool.AppendFile("log.txt", "first\n")).RootElement;
        Assert.True(root.GetProperty("created").GetBoolean());
        Assert.Equal("first\n", tool.ReadFile("log.txt"));
    }

    [Fact]
    public void FileAppend_ExistingFile_AppendsContent()
    {
        var service = new FakeFileSystemService();
        service.AddFile("log.txt", "first\n");
        var tool = CreateTool(service);

        var root = Parse(tool.AppendFile("log.txt", "second\n")).RootElement;
        Assert.False(root.GetProperty("created").GetBoolean());
        Assert.Equal("first\nsecond\n", tool.ReadFile("log.txt"));
    }

    // ---------- file_search ----------

    [Fact]
    public void SearchFiles_Literal_FindsMatchingLines()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.cs", "hello world\nnothing\nHELLO again");
        service.AddFile("b.md", "unrelated");
        var tool = CreateTool(service);

        var root = Parse(tool.SearchFiles(".", "hello")).RootElement;
        Assert.Equal(2, root.GetProperty("matchCount").GetInt32());
        var first = root.GetProperty("matches").EnumerateArray().First();
        Assert.Equal(1, first.GetProperty("lineNumber").GetInt32());
        Assert.Equal("hello world", first.GetProperty("lineText").GetString());
    }

    [Fact]
    public void SearchFiles_CaseSensitive_RespectsFlag()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.cs", "hello\nHELLO");
        var tool = CreateTool(service);

        Assert.Equal(1, Parse(tool.SearchFiles(".", "hello", caseSensitive: true)).RootElement.GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public void SearchFiles_Regex_MatchesPattern()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.cs", "error 404\nall good\nerror 500");
        var tool = CreateTool(service);

        Assert.Equal(2, Parse(tool.SearchFiles(".", @"error \d+", useRegex: true)).RootElement.GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public void SearchFiles_InvalidRegex_ReturnsInvalidArgument()
    {
        var tool = CreateTool();

        Assert.Equal("invalid_argument", CodeOf(tool.SearchFiles(".", "([", useRegex: true)));
    }

    [Fact]
    public void SearchFiles_FilePattern_FiltersFiles()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.cs", "hit");
        service.AddFile("b.md", "hit");
        var tool = CreateTool(service);

        var root = Parse(tool.SearchFiles(".", "hit", filePattern: "*.cs")).RootElement;
        Assert.Equal(1, root.GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public void SearchFiles_TruncationFlag_SetWhenCapped()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.txt", "x\nx\nx\nx");
        var tool = CreateTool(service);

        var root = Parse(tool.SearchFiles(".", "x", maxResults: 2)).RootElement;
        Assert.Equal(2, root.GetProperty("matchCount").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void SearchFiles_BinaryFile_Skipped()
    {
        var service = new FakeFileSystemService();
        service.AddBinaryFile("blob.bin");
        var tool = CreateTool(service);

        Assert.Equal(0, Parse(tool.SearchFiles(".", "bin")).RootElement.GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public void FileCopy_Basic_CopiesAndLeavesSource()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.txt", "data");
        var tool = CreateTool(service);

        var root = Parse(tool.CopyFile("a.txt", "sub/b.txt")).RootElement;
        Assert.False(root.GetProperty("overwrote").GetBoolean());
        Assert.Equal(1, root.GetProperty("entriesCopied").GetInt32());
        Assert.Equal("data", tool.ReadFile("a.txt"));
        Assert.Equal("data", tool.ReadFile("sub/b.txt"));
    }

    [Fact]
    public void FileCopy_ExistingDestinationNoOverwrite_ReturnsAlreadyExists()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.txt", "new");
        service.AddFile("b.txt", "original");
        var tool = CreateTool(service);

        Assert.Equal("already_exists", CodeOf(tool.CopyFile("a.txt", "b.txt")));
        Assert.Equal("original", tool.ReadFile("b.txt"));
    }

    [Fact]
    public void FileCopy_Overwrite_ReplacesDestination()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.txt", "new");
        service.AddFile("b.txt", "original");
        var tool = CreateTool(service);

        Assert.True(Parse(tool.CopyFile("a.txt", "b.txt", overwrite: true)).RootElement.GetProperty("overwrote").GetBoolean());
        Assert.Equal("new", tool.ReadFile("b.txt"));
        Assert.Equal("new", tool.ReadFile("a.txt"));
    }

    [Fact]
    public void DirectoryCopy_Basic_CopiesTreeAndCountsFiles()
    {
        var service = new FakeFileSystemService();
        service.AddFile("src/a.txt", "1");
        service.AddFile("src/sub/b.txt", "2");
        var tool = CreateTool(service);

        var root = Parse(tool.CopyDirectory("src", "dst")).RootElement;
        Assert.Equal(2, root.GetProperty("entriesCopied").GetInt32());
        Assert.Equal("1", tool.ReadFile("dst/a.txt"));
        Assert.Equal("2", tool.ReadFile("dst/sub/b.txt"));
        Assert.Equal("1", tool.ReadFile("src/a.txt"));
    }

    [Fact]
    public void DirectoryCopy_ExistingDestinationNoOverwrite_ReturnsAlreadyExists()
    {
        var service = new FakeFileSystemService();
        service.AddFile("src/a.txt", "1");
        service.AddDirectory("dst");
        var tool = CreateTool(service);

        Assert.Equal("already_exists", CodeOf(tool.CopyDirectory("src", "dst")));
    }

    [Fact]
    public void DirectoryCopy_IntoItself_ReturnsInvalidArgument()
    {
        var service = new FakeFileSystemService();
        service.AddDirectory("src");
        var tool = CreateTool(service);

        Assert.Equal("invalid_argument", CodeOf(tool.CopyDirectory("src", "src/inner")));
    }

    [Fact]
    public void Copy_DestinationOutsideRoot_ReturnsSandboxError()
    {
        var service = new FakeFileSystemService();
        service.AddFile("a.txt", "x");
        var tool = CreateTool(service);

        Assert.Equal("sandbox_violation", CodeOf(tool.CopyFile("a.txt", "../out.txt")));
        Assert.Equal("sandbox_violation", CodeOf(tool.CopyDirectory(".", "../out")));
    }

    [Fact]
    public void Copy_Root_ReturnsInvalidArgument()
    {
        var tool = CreateTool();

        Assert.Equal("invalid_argument", CodeOf(tool.CopyDirectory(".", "copy-of-root")));
    }
}
