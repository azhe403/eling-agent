using Eling.Core.Logging;
using Serilog.Core;
using Serilog.Events;

namespace Eling.Backend.Logging;

/// <summary>
/// A stable <see cref="ILogEventSink"/> registered in DI whose inner
/// <see cref="RollingDailyFileSink"/> can be swapped without changing the
/// registered instance.
/// </summary>
/// <remarks>
/// The forwarder is the object registered via <c>services.AddSingleton</c>;
/// only <em>its</em> reference is ever handed to <c>Serilog</c>.
/// The inner sink is the swappable target — a format switch replaces it
/// while the forwarder (and therefore the <c>Serilog</c> logger) remains
/// untouched.
/// </remarks>
public sealed class LogFormatForwardingSink : ILogEventSink, IDisposable
{
    private readonly object _gate = new();
    private RollingDailyFileSink? _inner;

    public LogFormatForwardingSink(RollingDailyFileSink? initialInner = null)
    {
        _inner = initialInner;
    }

    /// <summary>The current inner sink, or <c>null</c> if none is set.</summary>
    public RollingDailyFileSink? Inner
    {
        get { lock (_gate) { return _inner; } }
    }

    /// <summary>
    /// Replaces the inner sink. The caller owns disposing the old sink if
    /// it should be closed; this method only swaps the reference.
    /// </summary>
    public void SetInner(RollingDailyFileSink? inner)
    {
        lock (_gate) { _inner = inner; }
    }

    public void Emit(LogEvent evt)
    {
        RollingDailyFileSink? i;
        lock (_gate) { i = _inner; }
        i?.Emit(evt);
    }

    public void Dispose()
    {
        RollingDailyFileSink? i;
        lock (_gate) { i = _inner; _inner = null; }
        i?.Dispose();
    }
}
