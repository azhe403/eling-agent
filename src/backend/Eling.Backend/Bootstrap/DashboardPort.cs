using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// Resolves and probes the loopback port that the dashboard subsystem owns.
/// In the merged single-binary architecture the *first* backend to launch binds
/// the port and serves REST + UI; every subsequent backend (peer) simply probes
/// whether the port is already listening and may later take over via
/// <see cref="Eling.Backend.PortMonitor"/> if the owner dies.
/// </summary>
public static class DashboardPort
{
    /// <summary>Default staging port (global dashboard). 4417 in dev mode is set via <c>ELING_DASHBOARD_PORT</c>.</summary>
    public const int Default = 4317;

    /// <summary>Resolves the dashboard port from <c>ELING_DASHBOARD_PORT</c> env var, falling back to <see cref="Default"/>.</summary>
    public static int Resolve() =>
        int.TryParse(Environment.GetEnvironmentVariable("ELING_DASHBOARD_PORT"), out var port) && port > 0
            ? port
            : Default;

    /// <summary>
    /// Resolves the peer takeover poll interval from <c>ELING_TAKEOVER_MS</c> env var,
    /// falling back to 3000ms. The interval is how often a MCP-only peer checks whether
    /// the dashboard port has been freed by the owner dying.
    /// </summary>
    public static TimeSpan ResolveTakeoverMs()
    {
        var raw = Environment.GetEnvironmentVariable("ELING_TAKEOVER_MS");
        if (int.TryParse(raw, out var ms) && ms > 0) return TimeSpan.FromMilliseconds(ms);
        return TimeSpan.FromSeconds(3);
    }

    /// <summary>
    /// True when something is already listening on <paramref name="port"/> on the
    /// loopback interface. Probes via a non-blocking TCP socket connect on IPv4
    /// and IPv6 loopback, falling back to active listeners inspection.
    /// </summary>
    public static bool IsLoopbackListening(int port)
    {
        if (CanConnectToLoopback(IPAddress.Loopback, port))
            return true;

        if (Socket.OSSupportsIPv6 && CanConnectToLoopback(IPAddress.IPv6Loopback, port))
            return true;

        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            if (listeners.Any(ep => IPAddress.IsLoopback(ep.Address) && ep.Port == port))
                return true;
        }
        catch
        {
            // Best-effort inspection fallback
        }

        return false;
    }

    private static bool CanConnectToLoopback(IPAddress address, int port)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;
            var endpoint = new IPEndPoint(address, port);
            var result = socket.BeginConnect(endpoint, null, null);
            var completed = result.AsyncWaitHandle.WaitOne(150);
            if (!completed)
            {
                socket.Close();
                return false;
            }
            socket.EndConnect(result);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
