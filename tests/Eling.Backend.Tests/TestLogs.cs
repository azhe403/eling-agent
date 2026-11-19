using Microsoft.Extensions.Logging;

namespace Eling.Backend.Tests;

/// <summary>
/// Shared test-side logger for teardown/cleanup paths that swallowed failures silently.
/// Deliberately dependency-free: implements <see cref="ILogger"/> directly over the
/// Microsoft.Extensions.Logging.Abstractions package and writes to the console, so the
/// MEL convention holds in tests without pulling the logging implementation (or a
/// provider package) into the test project. Failures stay visible in test diagnostics
/// instead of compiling to nothing, as Debug.WriteLine would in Release.
/// </summary>
internal static class TestLogs
{
    private static readonly ILogger Logger = new ConsoleLogger();

    public static void Log(string message, Exception? exception = null) => Logger.LogDebug(exception, "{Message}", message);

    private sealed class ConsoleLogger : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Console.WriteLine($"[{logLevel}] {formatter(state, exception)}{(exception is null ? string.Empty : $" | {exception.GetType().Name}: {exception.Message}")}");
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
