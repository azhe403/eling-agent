using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// Helpers for the two Kestrel bind outcomes we explicitly handle:
/// the port was free at probe time but got taken by a peer between probe
/// and bind (race condition), and the generic AddressAlreadyInUse socket
/// error from Kestrel startup.
/// </summary>
public static class BindFailure
{
    public static bool IsAddressInUse(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse)
                return true;
        }
        return false;
    }

    public static void Log(ILogger logger, int port)
    {
        logger.LogWarning(
            "127.0.0.1:{Port} was bound by a peer between probe and start; this instance exits cleanly without serving HTTP.",
            port);
    }
}
