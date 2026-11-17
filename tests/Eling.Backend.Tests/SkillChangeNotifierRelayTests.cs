using System.Net;
using System.Net.Sockets;
using Eling.Backend.Services;

namespace Eling.Backend.Tests;

public sealed class SkillChangeNotifierRelayTests
{
    private static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task NotifyAsync_ClosedPort_SwallowsWithoutThrowing()
    {
        // Arrange — nothing listens here, connection refused is immediate
        var notifier = new HttpCoordinatorSkillChangeNotifier(ClosedPort());

        // Act
        var ex = await Record.ExceptionAsync(() => notifier.NotifyAsync());

        // Assert — fire-and-forget: dashboard optional, never throws
        Assert.Null(ex);
    }

    [Fact]
    public async Task NotifyAsync_CanceledToken_SwallowsWithoutThrowing()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var notifier = new HttpCoordinatorSkillChangeNotifier(ClosedPort());

        // Act
        var ex = await Record.ExceptionAsync(() => notifier.NotifyAsync("mutation", cts.Token));

        // Assert
        Assert.Null(ex);
    }
}
