using Eling.Backend.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

public sealed class ProcessMetricsTests
{
    private static ProcessMetricsTracker Tracker()
        => new(NullLogger<ProcessMetricsTracker>.Instance);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Sample_InvalidPid_ReturnsNull(int pid)
    {
        // Act
        var actual = Tracker().Sample(pid);

        // Assert
        Assert.Null(actual);
    }

    [Fact]
    public void Sample_UnknownPid_ReturnsNull()
    {
        // Act — no real process owns this id
        var actual = Tracker().Sample(int.MaxValue);

        // Assert
        Assert.Null(actual);
    }

    [Fact]
    public void Sample_OwnProcess_ReturnsMetrics()
    {
        // Arrange
        var pid = Environment.ProcessId;

        // Act
        var actual = Tracker().Sample(pid);

        // Assert
        Assert.NotNull(actual);
        Assert.True(actual.MemoryBytes > 0);
        Assert.InRange(actual.CpuPercent, 0, 100);
    }

    [Fact]
    public void Sample_SecondImmediateSample_ReportsZeroCpu()
    {
        // Arrange
        var tracker = Tracker();
        var pid = Environment.ProcessId;

        // Act — two samples back-to-back: elapsed < 0.2s, no cpu delta computed
        tracker.Sample(pid);
        var actual = tracker.Sample(pid);

        // Assert
        Assert.NotNull(actual);
        Assert.Equal(0, actual.CpuPercent);
    }
}
