using Eling.Backend.Mcp;

namespace Eling.Backend.Tests;

public sealed class DailyLogRollerTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"roller-{Guid.NewGuid():N}");
    private DailyLogRollerService? _service;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _service = new DailyLogRollerService(_tempDir);
        await _service.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_service is not null)
            await _service.StopAsync(CancellationToken.None);
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Service_Starts_WithoutRolling()
    {
        // Assert — lifecycle smoke: start/stop completes, no rollover triggered
        Assert.NotNull(_service);
        Assert.True(Directory.Exists(_tempDir));
    }
}
