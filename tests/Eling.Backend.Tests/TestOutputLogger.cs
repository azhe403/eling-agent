using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// Bridges <see cref="ILogger{TCategoryName}"/> to xUnit's
/// <c>ITestOutputHelper</c> so a service under test writes its own diagnostics
/// into the test output.
/// <para>
/// The recorded project rule is that every catch block must write a log and
/// that tests reach for ITestOutputHelper. Handing the service under test a
/// <c>NullLogger</c> would satisfy that letter while hiding precisely the
/// failures the test exists to catch: a batch-index error swallowed inside the
/// watcher is invisible either way, so its logs are routed somewhere readable.
/// </para>
/// <para>
/// The test must call <see cref="Stop"/> before disposing the service. xUnit v2's
/// TestOutputHelper throws once the test is no longer active, and a
/// FileSystemWatcher handler or a catch inside the service can still log after
/// the test method returns — which would turn a swallowed failure into a new
/// unhandled exception on a background thread. After <see cref="Stop"/>, writes
/// are dropped rather than thrown.
/// </para>
/// </summary>
public sealed class TestOutputLogger<TCategoryName> : ILogger<TCategoryName>
{
    private readonly ITestOutputHelper _output;
    private volatile bool _stopped;

    public TestOutputLogger(ITestOutputHelper output) => _output = output;

    /// <summary>Stops forwarding to the test output. Idempotent.</summary>
    public void Stop() => _stopped = true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>Warnings and errors only; informational chatter is not signal here.</summary>
    public bool IsEnabled(LogLevel logLevel) => !_stopped && logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var detail = exception is null ? string.Empty : $" -> {exception}";
        _output.WriteLine(
            $"[{logLevel}] [{typeof(TCategoryName).Name}] {formatter(state, exception)}{detail}");
    }
}
