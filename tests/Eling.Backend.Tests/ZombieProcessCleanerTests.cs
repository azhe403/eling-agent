using System.Net;
using Eling.Backend.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

public sealed class ZombieProcessCleanerTests
{
    [Fact]
    public async Task IsHealthyAsync_False_WhenPortClosed()
    {
        // Pick an unused port
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var healthy = await ZombieProcessCleaner.IsHealthyAsync(port, CancellationToken.None);

        Assert.False(healthy);
    }

    [Fact]
    public async Task IsHealthyAsync_True_WhenHealthEndpointReturns200()
    {
        using var listener = new HttpListener();
        var port = 49152 + Random.Shared.Next(1000, 10000);
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                var context = await listener.GetContextAsync();
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
            catch
            {
                // Ignored on teardown
            }
        });

        try
        {
            var healthy = await ZombieProcessCleaner.IsHealthyAsync(port, CancellationToken.None);
            Assert.True(healthy);
        }
        finally
        {
            listener.Stop();
            await serverTask;
        }
    }

    [Fact]
    public async Task TryClearZombieOnPortAsync_ReturnsFalse_WhenPortHealthy()
    {
        using var listener = new HttpListener();
        var port = 49152 + Random.Shared.Next(1000, 10000);
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                var context = await listener.GetContextAsync();
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
            catch
            {
                // Ignored on teardown
            }
        });

        try
        {
            var cleared = await ZombieProcessCleaner.TryClearZombieOnPortAsync(
                port,
                NullLogger.Instance,
                CancellationToken.None);

            Assert.False(cleared);
        }
        finally
        {
            listener.Stop();
            await serverTask;
        }
    }
}
