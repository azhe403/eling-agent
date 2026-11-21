using System.Net.Sockets;
using Serilog.Core;
using Serilog.Events;
using System.Linq;

namespace Eling.Core.Logging;

/// <summary>
/// Demotes one known-duplicate framework event to <see cref="LogEventLevel.Warning"/>
/// and drops the stack trace: the generic host logs
/// <c>Hosting failed to start</c> (EventId 11, HostedServiceStartupFaulted)
/// with the full Kestrel bind stack whenever the bind loses the
/// probe-then-bind race for the dashboard port. The HTTP loop catches the
/// rethrown exception and logs the same condition as a one-line Warning
/// (or promotes the process to owner after zombie recovery), so the Error
/// copy is noise. Every other event passes through untouched.
/// </summary>
public sealed class HostingFailureDemotingSink : ILogEventSink
{
    private const string SourceContextName = "Microsoft.Extensions.Hosting.Internal.Host";
    private const string MessageTemplateText = "Hosting failed to start";

    private readonly ILogEventSink _inner;

    public HostingFailureDemotingSink(ILogEventSink inner) => _inner = inner;

    public void Emit(LogEvent logEvent)
    {
        if (IsBindRaceDuplicate(logEvent))
        {
            var demoted = new LogEvent(
                logEvent.Timestamp,
                LogEventLevel.Warning,
                exception: null,
                logEvent.MessageTemplate,
                logEvent.Properties.Select(pair => new LogEventProperty(pair.Key, pair.Value)));
            _inner.Emit(demoted);
            return;
        }

        _inner.Emit(logEvent);
    }

    private static bool IsBindRaceDuplicate(LogEvent logEvent)
        => logEvent.Level == LogEventLevel.Error
            && string.Equals(logEvent.MessageTemplate.Text, MessageTemplateText, StringComparison.Ordinal)
            && logEvent.Exception is not null
            && IsAddressAlreadyInUse(logEvent.Exception)
            && logEvent.Properties.TryGetValue("SourceContext", out var value)
            && value is ScalarValue scalar
            && string.Equals(scalar.Value as string, SourceContextName, StringComparison.Ordinal);

    private static bool IsAddressAlreadyInUse(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return true;
            }
        }

        return false;
    }
}
