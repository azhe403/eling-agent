using System.Text.Json;

using Eling.Core.Database;
using Eling.Core.Serialization;

namespace Eling.Core.Tests;

/// <summary>
/// The gate is the file mechanics every read cache shares: read it, write it
/// atomically, and hold the lock while doing so. A reader must never see a
/// half-written file, and an unreadable file must cost a refetch rather than
/// an exception.
/// </summary>
public sealed class CacheFileGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "eling-cache-gate-tests", Guid.NewGuid().ToString("N"));

    private sealed record Entry(
        string Name,
        int Value);

    private CacheFileGate NewGate(string name)
    {
        Directory.CreateDirectory(_dir);
        return new CacheFileGate(
            Path.Combine(_dir, $"{name}.json"),
            $@"Global\eling-test-gate-{name}-{Guid.NewGuid():N}",
            null);
    }

    [Fact]
    public void Load_AnAbsentFile_ReturnsTheEmptyValue()
    {
        var gate = NewGate("absent");

        Assert.Equal("fallback", gate.Load(() => "fallback"));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var gate = NewGate("roundtrip");
        gate.Save(new Entry("a", 1));

        var loaded = gate.Load(() => (Entry?)null);

        Assert.NotNull(loaded);
        Assert.Equal("a", loaded!.Name);
        Assert.Equal(1, loaded.Value);
    }

    [Fact]
    public void Load_AnUnreadableFile_ReturnsTheEmptyValue()
    {
        var gate = NewGate("broken");
        File.WriteAllText(gate.Path, "{ not json");

        Assert.Equal("fallback", gate.Load(() => "fallback"));
    }

    [Fact]
    public void Load_ABlankFile_ReturnsTheEmptyValue()
    {
        var gate = NewGate("blank");
        File.WriteAllText(gate.Path, "   ");

        Assert.Equal("fallback", gate.Load(() => "fallback"));
    }

    [Fact]
    public void Save_LeavesNoTempFileBehind()
    {
        var gate = NewGate("temp");
        gate.Save(new Entry("a", 1));

        Assert.False(File.Exists(gate.Path + ".tmp"));
        Assert.True(File.Exists(gate.Path));
    }

    [Fact]
    public void Save_ReplacesTheFileWholesale()
    {
        var gate = NewGate("replace");
        gate.Save(new Entry("a", 1));
        gate.Save(new Entry("b", 2));

        var loaded = gate.Load(() => (Entry?)null);

        Assert.Equal("b", loaded!.Name);
    }

    [Fact]
    public void TwoGatesWithTheSameName_SeeTheSameFile()
    {
        var name = "shared";
        var lockName = $@"Global\eling-test-gate-shared-{Guid.NewGuid():N}";
        var first = new CacheFileGate(Path.Combine(_dir, $"{name}.json"), lockName, null);
        var second = new CacheFileGate(Path.Combine(_dir, $"{name}.json"), lockName, null);

        first.Save(new Entry("a", 1));

        var loaded = second.Load(() => (Entry?)null);

        Assert.NotNull(loaded);
        Assert.Equal("a", loaded!.Name);
    }

    [Fact]
    public void TheFileOnDisk_IsPlainJson()
    {
        var gate = NewGate("plain");
        gate.Save(new Entry("a", 1));

        var json = File.ReadAllText(gate.Path);

        Assert.Contains("\"name\"", json);
        Assert.Equal("a", JsonSerializer.Deserialize<Entry>(json, JsonDefaults.Shared)!.Name);
    }

    [Fact]
    public void TheLockName_DiffersPerDirectoryAndPurpose()
    {
        // The name is derived, so two caches in one directory share a lock and
        // two caches in different directories do not collide on one file.
        var here = CacheFileLock.NameFor(_dir, "sql");
        var there = CacheFileLock.NameFor(Path.Combine(_dir, "other"), "sql");
        var other = CacheFileLock.NameFor(_dir, "redis");

        Assert.Equal(here, CacheFileLock.NameFor(_dir, "sql"));
        Assert.NotEqual(here, there);
        Assert.NotEqual(here, other);
    }

    [Fact]
    public void TheLockName_IsLegalForAnyPath()
    {
        // A path can be longer than a Windows object name allows, and can hold
        // characters a name may not; the hash keeps the name legal.
        var deep = Path.Combine(_dir, new string('x', 400), "cache.json");
        var name = CacheFileLock.NameFor(deep, "sql");

        Assert.True(name.Length < 64);
        Assert.DoesNotContain(deep, name);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
