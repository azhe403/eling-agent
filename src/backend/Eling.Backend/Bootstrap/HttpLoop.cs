using System.Net;
using System.Net.Sockets;
using Eling.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

public static class HttpLoop
{
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(
        AppServices shared,
        ProjectContext context,
        int dashboardPort,
        bool isDevMode,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var logger = loggerFactory.CreateLogger("Eling.Backend.HttpLoop");
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;
            WebApplication? app = null;
            try
            {
                var ownerMode = !DashboardPort.IsLoopbackListening(dashboardPort);
                logger.LogInformation(
                    "HTTP host attempt #{Attempt}: {Mode} 127.0.0.1:{Port}",
                    attempt, ownerMode ? "OWNER, binding Kestrel to" : "peer, polling for", dashboardPort);

                if (!ownerMode)
                {
                    var takeoverInterval = DashboardPort.ResolveTakeoverMs();
                    await PortMonitor.WaitForPortFreeAsync(dashboardPort, takeoverInterval, logger, cancellationToken);
                    logger.LogInformation(
                        "Peer promoting to owner on port {Port} (was waiting {Ms}ms per poll)",
                        dashboardPort, (int)takeoverInterval.TotalMilliseconds);
                    continue;
                }

                app = BuildWebApplication(shared, context, dashboardPort, isDevMode, loggerFactory);

                if (isDevMode)
                {
                    var feLogger = loggerFactory.CreateLogger("Eling.Backend.FrontendDev");
                    TrySpawnPnpmFrontend(context, dashboardPort, feLogger);
                }

                await app.RunAsync(cancellationToken);
                logger.LogInformation("HTTP host shut down cleanly on attempt #{Attempt}", attempt);
                return 0;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                logger.LogWarning(
                    "Port {Port} occupied (attempt #{Attempt}); retrying after jitter",
                    dashboardPort, attempt);
                await DelayWithJitterAsync(cancellationToken);
            }
            catch (IOException ex) when (BindFailure.IsAddressInUse(ex))
            {
                logger.LogWarning(
                    "Port {Port} bound by peer (attempt #{Attempt}, IOException): {Message}",
                    dashboardPort, attempt, ex.Message);
                await DelayWithJitterAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "HTTP host crashed on attempt #{Attempt}", attempt);
                return 1;
            }
            finally
            {
                if (app is not null)
                {
                    try
                    {
                        await app.DisposeAsync();
                    }
                    catch
                    {
                    }
                }
            }
        }

        return 0;
    }

    public static WebApplication BuildWebApplication(
        AppServices shared,
        ProjectContext context,
        int dashboardPort,
        bool isDevMode,
        ILoggerFactory loggerFactory
    )
    {
        var ownerMode = !DashboardPort.IsLoopbackListening(dashboardPort);
        var options = new WebApplicationOptions
        {
            WebRootPath = ownerMode
                ? Path.Combine(AppContext.BaseDirectory, "eling-dashboard-ui")
                : AppContext.BaseDirectory
        };

        var builder = WebApplication.CreateBuilder(options);

        if (!ownerMode)
        {
            builder.WebHost.ConfigureKestrel(_ =>
            {
            });
        }
        else
        {
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(IPAddress.Loopback, dashboardPort);
                k.Listen(IPAddress.IPv6Loopback, dashboardPort);
            });
        }

        DashboardServices.Register(builder.Services, context, shared, ownerMode);

        var app = builder.Build();

        if (ownerMode)
        {
            DashboardRoutes.Map(app);
        }

        return app;
    }

    private static async Task DelayWithJitterAsync(CancellationToken cancellationToken)
    {
        var jitter = Random.Shared.NextDouble();
        var delay = MinRetryDelay + (MaxRetryDelay - MinRetryDelay) * jitter;
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static void TrySpawnPnpmFrontend(
        ProjectContext context,
        int backendPort,
        ILogger logger
    )
        => FrontendDevSpawner.TrySpawn(context, backendPort, logger);
}
