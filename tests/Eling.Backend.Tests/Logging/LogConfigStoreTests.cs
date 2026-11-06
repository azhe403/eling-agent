using Eling.Backend.Logging;
using Eling.Core.Logging;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests.Logging;

/// <summary>
/// Log format is per-user runtime configuration, so these tests verify the
/// template/corrupt behaviour and cross-process round-trips.
/// </summary>
public sealed class LogConfigStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"eling-logcfg-{Guid.NewGuid():N}");

    private LogConfigStore NewStore()
    {
        Directory.CreateDirectory(_root);
        return new LogConfigStore(new UserScope(_root), NullLogger<LogConfigStore>.Instance);
    }

    private string LogConfigPath => Path.Combine(_root, "config", "log.json");

    /// <summary>Writes the config file directly, simulating another process mutating it.</summary>
    private void WriteFormat(string? format)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogConfigPath)!);
        var value = format is null
            ? "null"
            : $"\"{format}\"";
        File.WriteAllText(LogConfigPath, $"{{ \"format\": {value} }}");
    }

    [Fact]
    public void Ctor_WritesTemplate_WhenFileAbsent()
    {
        var store = NewStore();

        Assert.True(File.Exists(LogConfigPath));
        Assert.Equal(LogFormat.Text, store.GetFormat());

        var raw = File.ReadAllText(LogConfigPath);
        Assert.Contains("\"format\"", raw);
        Assert.Contains("\"text\"", raw);
    }

    [Fact]
    public void Ctor_LeavesCorruptFileUntouched()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        var path = Path.Combine(_root, "config", "log.json");
        File.WriteAllText(path, "{ not json");

        var store = NewStore();

        Assert.Equal(LogFormat.Text, store.GetFormat());
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void GetFormat_UnknownFormatValue_FallsBackToText()
    {
        NewStore();
        WriteFormat("xml");

        Assert.Equal(LogFormat.Text, NewStore().GetFormat());
    }

    [Fact]
    public void GetFormat_NullFormatValue_FallsBackToText()
    {
        NewStore();
        WriteFormat(null);

        Assert.Equal(LogFormat.Text, NewStore().GetFormat());
    }

    [Theory]
    [InlineData(LogFormat.Text)]
    [InlineData(LogFormat.Jsonl)]
    public void UpdateFormat_PersistsAndASecondStoreSeesIt(LogFormat format)
    {
        var store = NewStore();

        var returned = store.UpdateFormat(format);

        Assert.Equal(format, returned);
        Assert.Equal(format, NewStore().GetFormat());
    }

    [Fact]
    public void UpdateFormat_WritesLowercaseEnumName()
    {
        NewStore().UpdateFormat(LogFormat.Jsonl);

        var raw = File.ReadAllText(LogConfigPath);
        Assert.Contains("\"jsonl\"", raw);
    }

    [Fact]
    public void Format_Property_TracksDiskChange()
    {
        var store = NewStore();
        WriteFormat("jsonl");

        Assert.Equal(LogFormat.Jsonl, store.Format);
        Assert.Equal(LogFormat.Jsonl, store.GetFormat());
    }

    [Fact]
    public void GetFormat_ReloadsWhenAnotherProcessWrote()
    {
        var store = NewStore();
        store.UpdateFormat(LogFormat.Jsonl);
        WriteFormat("text");

        Assert.Equal(LogFormat.Text, store.GetFormat());
    }

    [Fact]
    public void UpdateFormat_AfterExternalWrite_DoesNotRevertIt()
    {
        var store = NewStore();
        store.UpdateFormat(LogFormat.Jsonl);
        WriteFormat("text");

        // The external write to "text" must not be clobbered by a stale
        // snapshot of this store: it is re-read, and the next write from
        // this store keeps the external value intact.
        var returned = store.UpdateFormat(LogFormat.Text);

        Assert.Equal(LogFormat.Text, returned);
        var raw = File.ReadAllText(LogConfigPath);
        Assert.Contains("\"text\"", raw);
    }

    [Fact]
    public void GetFormat_WhenExternallyCorrupt_KeepsLastGoodSnapshot()
    {
        var store = NewStore();
        store.UpdateFormat(LogFormat.Jsonl);
        File.WriteAllText(LogConfigPath, "{ not json");

        Assert.Equal(LogFormat.Jsonl, store.GetFormat());
        Assert.Equal("{ not json", File.ReadAllText(LogConfigPath));
    }

    [Fact]
    public void GetFormat_WhenFileDeletedExternally_KeepsLastGoodSnapshot()
    {
        var store = NewStore();
        store.UpdateFormat(LogFormat.Jsonl);
        File.Delete(LogConfigPath);

        Assert.Equal(LogFormat.Jsonl, store.GetFormat());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
