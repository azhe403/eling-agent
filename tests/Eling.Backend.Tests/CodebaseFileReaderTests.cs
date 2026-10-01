using Eling.Core.Codebase;

namespace Eling.Backend.Tests;

/// <summary>
/// The file reader turns a caller-supplied relative path into a real disk read,
/// so the containment guard is the whole test surface that matters: every case
/// here is a way someone could otherwise read a file outside the workspace.
/// </summary>
public sealed class CodebaseFileReaderTests : IDisposable
{
    private readonly string _root;
    private readonly CodebaseFileReader _sut = new();

    public CodebaseFileReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-filereader-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "app.cs"), "line one\nline two\n");
    }

    [Fact]
    public async Task ReadAsync_WithPathInsideRoot_ReturnsContent()
    {
        var read = await _sut.ReadAsync(_root, "src/app.cs");

        Assert.Equal(CodebaseFileReadStatus.Ok, read.Status);
        Assert.Equal("line one\nline two\n", read.Content);
    }

    [Fact]
    public async Task ReadAsync_WithBackslashSeparatedPath_ResolvesTheSameFile()
    {
        // Index paths are stored forward-slashed but a caller on Windows may
        // send either separator; both must land on the same file.
        var read = await _sut.ReadAsync(_root, @"src\app.cs");

        Assert.Equal(CodebaseFileReadStatus.Ok, read.Status);
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../outside.cs")]
    [InlineData("..")]
    public async Task ReadAsync_WithPathEscapingRoot_Throws(string path)
    {
        // GetFullPath collapses these happily — that is exactly why the
        // containment check has to be a separate test on the resolved path.
        var act = () => _sut.ReadAsync(_root, path);

        await Assert.ThrowsAsync<ArgumentException>(act);
    }

    [Fact]
    public async Task ReadAsync_WithAbsolutePathOutsideRoot_Throws()
    {
        // Path.Combine discards the root when the second part is rooted, so an
        // absolute "relative path" would escape the combine entirely.
        var outside = Path.Combine(Path.GetTempPath(), "eling-filereader-outside.cs");
        File.WriteAllText(outside, "secret");

        var act = () => _sut.ReadAsync(_root, outside);

        await Assert.ThrowsAsync<ArgumentException>(act);
    }

    [Fact]
    public async Task ReadAsync_WithMissingFile_ReportsNotFound()
    {
        var read = await _sut.ReadAsync(_root, "src/nope.cs");

        Assert.Equal(CodebaseFileReadStatus.NotFound, read.Status);
        Assert.Null(read.Content);
    }

    [Fact]
    public async Task ReadAsync_WithBinaryFile_ReportsBinary()
    {
        var path = Path.Combine(_root, "blob.bin");
        await File.WriteAllBytesAsync(path, [0x41, 0x00, 0x42]);

        var read = await _sut.ReadAsync(_root, "blob.bin");

        Assert.Equal(CodebaseFileReadStatus.Binary, read.Status);
        Assert.Null(read.Content);
    }

    [Fact]
    public async Task ReadAsync_WithFileOverTheCap_ReportsTooLarge()
    {
        // The cap is deliberately above the indexer's own, so a file the
        // indexer skipped for size can still be read here.
        Assert.True(CodebaseFileReader.MaxBytes > 512 * 1024);

        var path = Path.Combine(_root, "big.txt");
        await File.WriteAllTextAsync(path, new string('x', checked((int)CodebaseFileReader.MaxBytes) + 1));

        var read = await _sut.ReadAsync(_root, "big.txt");

        Assert.Equal(CodebaseFileReadStatus.TooLarge, read.Status);
        Assert.Null(read.Content);
    }

    [Fact]
    public async Task ReadAsync_WithBlankPath_Throws()
    {
        var act = () => _sut.ReadAsync(_root, "   ");

        await Assert.ThrowsAsync<ArgumentException>(act);
    }

    public void Dispose()
    {
        CodebaseRebuildScopeTests.TryDelete(_root);
        CodebaseRebuildScopeTests.TryDelete(Path.Combine(Path.GetTempPath(), "eling-filereader-outside.cs"));
    }
}
