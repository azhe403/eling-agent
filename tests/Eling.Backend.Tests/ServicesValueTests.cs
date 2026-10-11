using Eling.Backend.Agent.Ports;
using Eling.Backend.Services;

namespace Eling.Backend.Tests;

public sealed class ServicesValueTests
{
    [Fact]
    public void Metrics_Equality_ByValue()
    {
        // Act
        var left = new RuntimeProcessMetrics(1024, 12.5);
        var right = new RuntimeProcessMetrics(1024, 12.5);

        // Assert
        Assert.Equal(left, right);
    }

    [Fact]
    public void Metrics_Inequality_WhenFieldsDiffer()
    {
        // Act
        var left = new RuntimeProcessMetrics(1024, 12.5);
        var right = new RuntimeProcessMetrics(2048, 12.5);

        // Assert
        Assert.NotEqual(left, right);
    }

    [Fact]
    public void Probe_Equality_ByValue()
    {
        // Act
        var left = new ProviderProbe(true, "ok");
        var right = new ProviderProbe(true, "ok");

        // Assert
        Assert.Equal(left, right);
    }

    [Fact]
    public void Probe_PreservesFields()
    {
        // Act
        var probe = new ProviderProbe(false, "connection refused");

        // Assert
        Assert.False(probe.Ok);
        Assert.Equal("connection refused", probe.Message);
    }
}
