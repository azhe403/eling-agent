using System.Diagnostics;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// Returns the current process start time as <see cref="DateTimeOffset"/>.
/// Falls back to "now" when the underlying <c>Process.StartTime</c> probe
/// is denied (e.g. on some sandboxed environments).
/// </summary>
public static class ProcessStartTime
{
    public static DateTimeOffset Get()
    {
        try
        {
            return new DateTimeOffset(Process.GetCurrentProcess().StartTime);
        }
        catch
        {
            return DateTimeOffset.Now;
        }
    }
}
