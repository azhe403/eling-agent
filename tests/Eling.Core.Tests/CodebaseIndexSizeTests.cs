using Eling.Core.Codebase;

namespace Eling.Core.Tests;

/// <summary>
/// The size the dashboard reports for an index is the database file plus its
/// write-ahead log — the WAL holds committed writes that have not been
/// checkpointed yet, so leaving it out understates what the index costs on
/// disk.
/// </summary>
public sealed class CodebaseIndexSizeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "eling-cbsize-" + Guid.NewGuid().ToString("N")[..8]);

    public CodebaseIndexSizeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string DbPath(string name = "workspace.db") => Path.Combine(_root, name);

    [Fact]
    public void Measure_DatabaseOnly_CountsTheFile()
    {
        var dbPath = DbPath();
        File.WriteAllBytes(dbPath, new byte[1000]);

        Assert.Equal(1000, CodebaseIndexSize.Measure(dbPath));
    }

    [Fact]
    public void Measure_WithWal_CountsBothFiles()
    {
        var dbPath = DbPath();
        File.WriteAllBytes(dbPath, new byte[1000]);
        File.WriteAllBytes(dbPath + "-wal", new byte[250]);

        Assert.Equal(1250, CodebaseIndexSize.Measure(dbPath));
    }

    [Fact]
    public void Measure_WithShmSidecar_IgnoresIt()
    {
        // The shared-memory file is a transient index over the WAL, rebuilt on
        // every open. It holds no data, so it is not part of the footprint.
        var dbPath = DbPath();
        File.WriteAllBytes(dbPath, new byte[1000]);
        File.WriteAllBytes(dbPath + "-shm", new byte[32 * 1024]);

        Assert.Equal(1000, CodebaseIndexSize.Measure(dbPath));
    }

    [Fact]
    public void Measure_MissingFile_IsZero()
    {
        Assert.Equal(0, CodebaseIndexSize.Measure(DbPath("never-indexed.db")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Measure_BlankPath_IsZero(string dbPath)
    {
        Assert.Equal(0, CodebaseIndexSize.Measure(dbPath));
    }

    [Fact]
    public void Measure_EmptyDatabaseFile_IsZero()
    {
        var dbPath = DbPath("placeholder.db");
        File.WriteAllBytes(dbPath, []);

        Assert.Equal(0, CodebaseIndexSize.Measure(dbPath));
    }
}
