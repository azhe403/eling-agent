using System.Net.Sockets;

namespace Eling.Backend.Bootstrap;

internal static class DevModeDetector
{
    public static async Task<bool> IsDevModeAsync(CancellationToken cancellationToken = default)
    {
        var probe = FindRepoRootWithPnpm();
        return probe is not null && !await IsPortListeningAsync(4427, cancellationToken: cancellationToken);
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
