using System.Net;
using System.Net.Sockets;

namespace Eling.Backend.Tests;

public sealed class CoordinatorNotifierTests
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
        var notifier = new HttpCoordinatorMemoryChangeNotifier(ClosedPort());

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
        var notifier = new HttpCoordinatorMemoryChangeNotifier(ClosedPort());

        // Act
        var ex = await Record.ExceptionAsync(() => notifier.NotifyAsync(cancellationToken: cts.Token));

        // Assert
        Assert.Null(ex);
    }
}
