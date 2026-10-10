using Eling.Backend.Dtos;
using Eling.Backend.Mcp.Tools;
using Eling.Backend.Updates;
using Eling.Core.MemoryRecall;
using Xunit;

namespace Eling.Backend.Tests.Updates;

/// <summary>
/// The recall response carries the update notice only when an update is
/// available, and the notice path can never break recall itself.
/// </summary>
public sealed class UpdateNoticeBlockTests : IDisposable
{
    private static readonly UpdateStatus Available = new()
    {
        CurrentVersion = "0.1.0-pre.30",
        LatestVersion = "v0.1.0-pre.32",
        UpdateAvailable = true,
        ReleaseUrl = "https://example.test/pre32",
        CheckedAt = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero),
        Channel = "prerelease",
    };

    private static readonly UpdateStatus Quiet = new()
    {
        CurrentVersion = "0.1.0-pre.32",
        CheckedAt = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero),
        Channel = "prerelease",
    };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"eling-update-notice-{Guid.NewGuid():N}");

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

    private string NewWorkspace()
    {
        var workspace = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    [Fact]
    public async Task Recall_UpdateAvailable_CarriesNotice()
    {
        var tool = new MemoryRecallTool(new EmptyRecallService(), cwd: NewWorkspace(), updateChecker: new StubChecker(Available));

        var response = await tool.RecallAsync();

        Assert.NotNull(response.Update);
        Assert.Equal("v0.1.0-pre.32", response.Update.LatestVersion);
        Assert.Equal("https://example.test/pre32", response.Update.ReleaseUrl);
        Assert.Contains("install", response.Update.InstallHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recall_NoUpdate_OmitsNotice()
    {
        var tool = new MemoryRecallTool(new EmptyRecallService(), cwd: NewWorkspace(), updateChecker: new StubChecker(Quiet));

        var response = await tool.RecallAsync();

        Assert.Null(response.Update);
    }

    [Fact]
    public async Task Recall_WithoutChecker_OmitsNotice()
    {
        var tool = new MemoryRecallTool(new EmptyRecallService(), cwd: NewWorkspace());

        var response = await tool.RecallAsync();

        Assert.Null(response.Update);
    }

    [Fact]
    public void FromStatus_QuietStatus_ReturnsNull()
    {
        Assert.Null(UpdateNoticeDto.FromStatus(Quiet));
    }

    private sealed class EmptyRecallService : IMemoryRecallService
    {
        public Task<MemoryRecallResult> RecallAsync(
            MemoryRecallContext? context,
            int recallLimit = 10,
            int recentLimit = 10,
            string? scope = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryRecallResult([], [], [], new MemoryRecallStats(0, 0, 0, 0, 0)));
    }

    private sealed class StubChecker : IUpdateChecker
    {
        private readonly UpdateStatus _status;

        public StubChecker(UpdateStatus status)
        {
            _status = status;
        }

        public Task<UpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_status);

        public Task<UpdateStatus> CheckNowAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_status);
    }
}
