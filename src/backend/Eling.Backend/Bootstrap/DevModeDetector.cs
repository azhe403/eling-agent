using System.Net.Sockets;

namespace Eling.Backend.Bootstrap;

internal static class DevModeDetector
{
    /// <summary>
    /// Whether this repo has a frontend dev server worth supervising.
    /// </summary>
    /// <remarks>
    /// Deliberately independent of whether port 4427 is currently bound. The old
    /// gate used <c>!IsPortListeningAsync(4427)</c>, meaning "if something is
    /// already there, do not manage it" - which is precisely why the dashboard
    /// stopped auto-starting. <c>dotnet watch</c> restarts the backend without
    /// taking the <c>pnpm dev</c> process down, so the next generation saw 4427
    /// still bound and switched its own loop off, even though that process might
    /// have been dead or hung with nothing left to recover it. The decision
    /// belongs to the reconcile loop, not here.
    /// </remarks>
    public static bool IsDevMode()
    {
        var watchEnabled = FrontendReconcile.IsWatchEnabled(
            Environment.GetEnvironmentVariable("ELING_WATCH_DASHBOARD"));

        return FindRepoRootWithPnpm() is not null && watchEnabled;
    }

    public static async Task<bool> IsPortListeningAsync(
        int port,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout ?? TimeSpan.FromMilliseconds(200));
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static string? FindRepoRootWithPnpm()
    {
        var walker = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        for (var depth = 0; depth < 10 && walker is not null; depth++)
        {
            if (File.Exists(Path.Combine(walker.FullName, "package.json"))
                && File.Exists(Path.Combine(walker.FullName, "pnpm-lock.yaml")))
            {
                return walker.FullName;
            }

            walker = walker.Parent;
        }

        return null;
    }
}