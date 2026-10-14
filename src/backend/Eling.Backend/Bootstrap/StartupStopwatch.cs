using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// Process-lifetime stopwatch for MCP startup phases. The clock starts when the
/// type is first touched (the first statement of <c>Program.cs</c>) and every
/// <see cref="Mark"/> is recorded with its elapsed milliseconds.
/// </summary>
/// <remarks>
/// <para>
/// Marks are buffered until <see cref="AttachLogger"/> is called: the static
/// Serilog <c>Log</c> is never assigned in this host, so anything written
/// through it before the DI container exists is silently dropped. Attaching
/// flushes every buffered mark with the elapsed value recorded at mark time, so
/// the timeline stays honest even though emission was deferred.
/// </para>
/// <para>
/// The first MCP tools/list request fires <see cref="MarkMcpReady"/>. That is
/// the closest in-process proxy for "the client considers this server
/// connected" (initialize handshake done, catalog fetched), and it emits a
/// one-line summary with the per-phase breakdown so a slow connect can be
/// attributed to a phase instead of guessed from an aggregate client span.
/// </para>
/// </remarks>
public static class StartupStopwatch
{
    private sealed record PhaseMark(
        string Phase,
        long ElapsedMs);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static readonly List<PhaseMark> Marks = [];

    private static ILogger? _logger;
    private static int _unlogged;
    private static bool _summaryEmitted;
    private static bool _summaryDeferred;

    /// <summary>
    /// UTC process start time, for correlating with the client-side connect
    /// span (e.g. the MCP handshake duration in the host log). Deliberately the
    /// OS process start rather than the static-initialization time: under
    /// <c>dotnet watch</c> the process is spawned after a build that this
    /// stopwatch cannot see, so the process start is the earliest anchor this
    /// process can honestly report.
    /// </summary>
    public static DateTime StartedUtc { get; } = ReadProcessStartUtc();

    /// <summary>
    /// Records a startup phase. Before a logger is attached the mark is
    /// buffered; afterwards it is written to the structured log immediately.
    /// </summary>
    public static void Mark(string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        var elapsed = Clock.ElapsedMilliseconds;
        lock (Gate)
        {
            Marks.Add(new PhaseMark(phase, elapsed));
            _unlogged++;
            if (_logger is not null)
            {
                EmitPendingLocked();
            }
        }
    }

    /// <summary>
    /// Attaches the DI logger and flushes every mark recorded so far.
    /// Idempotent: the first logger wins for the process lifetime.
    /// </summary>
    public static void AttachLogger(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        lock (Gate)
        {
            if (_logger is not null)
            {
                return;
            }

            _logger = logger;
            EmitPendingLocked();

            if (_summaryDeferred)
            {
                _summaryDeferred = false;
                EmitSummaryLocked();
            }
        }
    }

    /// <summary>
    /// Records that the MCP layer answered its first tools/list request and,
    /// once per process, logs the phase-breakdown summary.
    /// </summary>
    public static void MarkMcpReady()
    {
        lock (Gate)
        {
            if (_summaryEmitted)
            {
                return;
            }

            _summaryEmitted = true;

            var elapsed = Clock.ElapsedMilliseconds;
            Marks.Add(new PhaseMark("mcp-catalog-done", elapsed));
            _unlogged++;

            if (_logger is null)
            {
                _summaryDeferred = true;
                return;
            }

            EmitPendingLocked();
            EmitSummaryLocked();
        }
    }

    private static DateTime ReadProcessStartUtc()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            // Some platforms/runtimes refuse StartTime; diagnostics must never
            // take the process down, so fall back to this moment.
            return DateTime.UtcNow;
        }
    }

    // Caller holds <see cref="Gate"/> and <see cref="_logger"/> is non-null.
    private static void EmitPendingLocked()
    {
        for (var i = Marks.Count - _unlogged; i < Marks.Count; i++)
        {
            var mark = Marks[i];
            _logger!.LogInformation("StartupTiming: phase={Phase} elapsedMs={ElapsedMs}", mark.Phase, mark.ElapsedMs);
        }

        _unlogged = 0;
    }

    // Caller holds <see cref="Gate"/> and <see cref="_logger"/> is non-null.
    private static void EmitSummaryLocked()
    {
        var snapshot = Marks.ToArray();
        var totalMs = Clock.ElapsedMilliseconds;
        var breakdown = string.Join("; ", DescribeBreakdown(snapshot));

        _logger!.LogInformation(
            "StartupTiming: SUMMARY mcp-ready totalMs={TotalMs} phases={Breakdown} startedUtc={StartedUtc:o}",
            totalMs,
            breakdown,
            StartedUtc);
    }

    private static IEnumerable<string> DescribeBreakdown(PhaseMark[] marks)
    {
        long previous = 0;
        string? previousPhase = null;

        foreach (var mark in marks)
        {
            // The first mark IS process entry; it owns the whole opening span
            // on its own instead of getting a self-referential arrow segment.
            yield return previousPhase is null
                ? $"{mark.Phase}={mark.ElapsedMs}ms"
                : $"{previousPhase}->{mark.Phase}={mark.ElapsedMs - previous}ms";

            previous = mark.ElapsedMs;
            previousPhase = mark.Phase;
        }
    }
}
