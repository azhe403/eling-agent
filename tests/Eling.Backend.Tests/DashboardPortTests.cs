using System.Net;
using System.Net.Sockets;
using Eling.Backend.Bootstrap;

namespace Eling.Backend.Tests;

public sealed class DashboardPortTests
{
    [Fact]
    public void Resolve_DefaultsTo4317_WhenEnvMissing()
    {
        // Arrange
        var previous = Environment.GetEnvironmentVariable("ELING_DASHBOARD_PORT");
        Environment.SetEnvironmentVariable("ELING_DASHBOARD_PORT", null);
        try
        {
            // Act
            var actual = DashboardPort.Resolve();

            // Assert
            Assert.Equal(4317, actual);
            Assert.Equal(DashboardPort.Default, actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELING_DASHBOARD_PORT", previous);
        }
    }

    [Theory]
    [InlineData("4417", 4417)]
    [InlineData("1", 1)]
    public void Resolve_HonoursValidEnv(string raw, int expected)
    {
        // Arrange
        var previous = Environment.GetEnvironmentVariable("ELING_DASHBOARD_PORT");
        Environment.SetEnvironmentVariable("ELING_DASHBOARD_PORT", raw);
        try
        {
            // Act
            var actual = DashboardPort.Resolve();

            // Assert
            Assert.Equal(expected, actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELING_DASHBOARD_PORT", previous);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-port")]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_FallsBackToDefault_OnInvalidEnv(string raw)
    {
        // Arrange
        var previous = Environment.GetEnvironmentVariable("ELING_DASHBOARD_PORT");
        Environment.SetEnvironmentVariable("ELING_DASHBOARD_PORT", raw);
        try
        {
            // Act
            var actual = DashboardPort.Resolve();

            // Assert
            Assert.Equal(DashboardPort.Default, actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELING_DASHBOARD_PORT", previous);
        }
    }

    [Fact]
    public void ResolveTakeoverMs_DefaultsTo3Seconds_WhenEnvMissing()
    {
        // Arrange
        var previous = Environment.GetEnvironmentVariable("ELING_TAKEOVER_MS");
        Environment.SetEnvironmentVariable("ELING_TAKEOVER_MS", null);
        try
        {
            // Act
            var actual = DashboardPort.ResolveTakeoverMs();

            // Assert
            Assert.Equal(TimeSpan.FromSeconds(3), actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELING_TAKEOVER_MS", previous);
        }
    }

    [Fact]
    public void ResolveTakeoverMs_HonoursValidEnv()
    {
        // Arrange
        var previous = Environment.GetEnvironmentVariable("ELING_TAKEOVER_MS");
        Environment.SetEnvironmentVariable("ELING_TAKEOVER_MS", "500");
        try
        {
            // Act
            var actual = DashboardPort.ResolveTakeoverMs();

            // Assert
            Assert.Equal(TimeSpan.FromMilliseconds(500), actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELING_TAKEOVER_MS", previous);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("junk")]
    [InlineData("")]
    public void ResolveTakeoverMs_FallsBackTo3Seconds_OnInvalidEnv(string raw)
    {
        // Arrange — only positive values win; anything else falls back to 3s
        var previous = Environment.GetEnvironmentVariable("ELING_TAKEOVER_MS");
        Environment.SetEnvironmentVariable("ELING_TAKEOVER_MS", raw);
        try
        {
            // Act
            var actual = DashboardPort.ResolveTakeoverMs();

            // Assert
            Assert.Equal(TimeSpan.FromSeconds(3), actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELING_TAKEOVER_MS", previous);
        }
    }

    [Fact]
    public void IsLoopbackListening_False_WhenNothingBound()
    {
        // Arrange — grab a free port then release it
        int port;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        // Act
        var actual = DashboardPort.IsLoopbackListening(port);

        // Assert
        Assert.False(actual);
    }

    [Fact]
    public void IsLoopbackListening_True_WhenBound()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Act
        var actual = DashboardPort.IsLoopbackListening(port);

        // Assert
        Assert.True(actual);
    }
}
