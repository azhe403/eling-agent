using Eling.Backend.Updates;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests.Updates;

/// <summary>
/// The cache is the update check's durable memory: corruption degrades to
/// "unknown" instead of throwing, and the in-memory snapshot swaps only after
/// a successful disk write so memory can never claim a status disk rejected.
/// </summary>
public sealed class FileUpdateCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"eling-update-cache-{Guid.NewGuid():N}");

    private string CachePath => Path.Combine(_root, "config", "update-check.json");

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

    [Fact]
    public void SaveStatus_Success_RoundTripsThroughDisk()
    {
        Directory.CreateDirectory(_root);
        var first = new FileUpdateCache(new UserScope(_root), NullLogger<FileUpdateCache>.Instance);
        var status = NewStatus("v0.1.0-pre.32");

        first.SaveStatus(status);

        var reopened = new FileUpdateCache(new UserScope(_root), NullLogger<FileUpdateCache>.Instance);
        Assert.Equal("v0.1.0-pre.32", reopened.GetStatus()?.LatestVersion);
    }

    [Fact]
    public void SaveStatus_FailedDiskWrite_KeepsPreviousSnapshotInMemory()
    {
        Directory.CreateDirectory(_root);
        var cache = new FileUpdateCache(new UserScope(_root), NullLogger<FileUpdateCache>.Instance);
        cache.SaveStatus(NewStatus("v0.1.0-pre.32"));

        // Force File.Move to fail: a directory now occupies the cache file path.
        File.Delete(CachePath);
        Directory.CreateDirectory(CachePath);

        cache.SaveStatus(NewStatus("v0.2.0"));

        Assert.Equal("v0.1.0-pre.32", cache.GetStatus()?.LatestVersion);
    }

    [Fact]
    public void CorruptCacheFile_LoadsAsUnknown()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, "{ not json");

        var cache = new FileUpdateCache(new UserScope(_root), NullLogger<FileUpdateCache>.Instance);

        Assert.Null(cache.GetStatus());
    }

    [Fact]
    public void MissingCacheFile_LoadsAsUnknown()
    {
        Directory.CreateDirectory(_root);

        var cache = new FileUpdateCache(new UserScope(_root), NullLogger<FileUpdateCache>.Instance);

        Assert.Null(cache.GetStatus());
    }

    private static UpdateStatus NewStatus(string latest) => new()
    {
        CurrentVersion = "0.1.0",
        LatestVersion = latest,
        UpdateAvailable = true,
        ReleaseUrl = $"https://example.test/{latest}",
        CheckedAt = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero),
        Channel = "prerelease",
    };
}
