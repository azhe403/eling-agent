using Eling.Core.Logging;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Eling.Backend.Logging;

/// <summary>
/// Applies a <see cref="LogFormat"/> to a running host: archives the outgoing
/// active file, swaps the inner sink of the DI-registered
/// <see cref="LogFormatForwardingSink"/>, and rebuilds the static
/// <see cref="Serilog.Log"/> logger so code that logs through it lands in the
/// new file.
/// </summary>
/// <remarks>
/// <para>
/// The switch is ordered so no log line is dropped:
/// <list type="number">
/// <item>the new sink is created (which opens the new active file);</item>
/// <item>the forwarder is pointed at it, so DI-routed loggers follow at once;</item>
/// <item>the static logger is rebuilt on it;</item>
/// <item>only then is the outgoing sink closed and its active file drained
/// into the archive for today.</item>
/// </list>
/// </para>
/// <para>
/// The draining step deletes the outgoing active file after appending its lines
/// to <c>{prefix}-{today}{ext}</c>: leaving it in place would duplicate today's
/// lines in the active file the next time the format is switched back, and the
/// archive is what retention prunes on schedule.
/// </para>
/// <para>
/// The rebuilt logger is disposed only if this class created it. The first
/// switch replaces the logger the host's DI container owns (<c>AddSerilog</c>
/// assigns it to the static <c>Log</c>); disposing that one would silence every
/// <c>ILogger&lt;T&gt;</c> in the process.
/// </para>
/// </remarks>
public sealed class LogFormatSwitcher : IDisposable
{
    private const int RetainedDays = 7;
    private const string ArchivePrefix = "backend";

    private readonly object _gate = new();
    private readonly LogFormatForwardingSink _forwarder;
    private readonly string _logsDirectory;
    private readonly string? _projectId;
    private readonly int _processId;
    private readonly ILogger<LogFormatSwitcher> _logger;

    private Serilog.Core.Logger? _installedLogger;

    public LogFormatSwitcher(
        LogFormatForwardingSink forwarder,
        string logsDirectory,
        string? projectId,
        int processId,
        ILogger<LogFormatSwitcher> logger)
    {
        ArgumentNullException.ThrowIfNull(forwarder);
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _forwarder = forwarder;
        _logsDirectory = Path.GetFullPath(logsDirectory);
        _projectId = projectId;
        _processId = processId;
        _logger = logger;
    }

    /// <summary>
    /// Switches the active sink to <paramref name="format"/>; a no-op when the
    /// current inner sink already writes that format.
    /// </summary>
    public void Apply(LogFormat format)
    {
        lock (_gate)
        {
            var outgoing = _forwarder.Inner;
            if (outgoing is not null && outgoing.Format == format)
            {
                return;
            }

            var activeFileName = ActiveFileNameFor(format);
            var newSink = ElingLoggingConfig.CreateSink(
                _logsDirectory,
                activeFileName,
                ArchivePrefix,
                RetainedDays,
                format);

            _forwarder.SetInner(newSink);
            InstallLogger(newSink);

            // The forwarder no longer routes anywhere near the outgoing sink, so
            // closing it here only releases its file handle.
            outgoing?.Dispose();

            if (outgoing is not null)
            {
                ArchiveActiveFile(outgoing.ActiveFilePath);
            }

            _logger.LogInformation(
                "Log format switched to {Format}; active file is now {ActiveFile}.",
                format,
                activeFileName);
        }
    }

    public void Dispose()
    {
        var installed = Interlocked.Exchange(ref _installedLogger, null);
        installed?.Dispose();
    }

    /// <summary>
    /// The logger configuration shared by the host's DI logger and every
    /// rebuilt static logger: Eling enrichment, the given file-sink facade, and
    /// the console sink on the default text template.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="ElingLoggingConfig.ConfigureElingDefaults"/>, which is
    /// typed to a concrete <see cref="RollingDailyFileSink"/> and so cannot take
    /// the forwarder. Keep the two in step.
    /// </remarks>
    internal static LoggerConfiguration ConfigureHostLogging(
        LoggerConfiguration configuration,
        ILogEventSink fileSink,
        string? projectId,
        int? processId)
    {
        var effectiveProjectId = string.IsNullOrWhiteSpace(projectId) ? "unknown" : projectId;
        var effectiveProcessId = processId ?? Environment.ProcessId;

        return configuration
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("ProcessId", effectiveProcessId)
            .Enrich.WithProperty("ProjectId", effectiveProjectId)
            .Enrich.WithProperty("CorrelationId", "-")
            .Enrich.With<AspNetEnvironmentEnricher>()
            .WriteTo.Sink(fileSink)
            .WriteTo.Console(
                outputTemplate: ElingLoggingConfig.DefaultOutputTemplate,
                standardErrorFromLevel: LogEventLevel.Verbose);
    }

    private void InstallLogger(RollingDailyFileSink sink)
    {
        var rebuilt = ConfigureHostLogging(new LoggerConfiguration(), sink, _projectId, _processId)
            .CreateLogger();

        Serilog.Log.Logger = rebuilt;

        // Written after the assignment on purpose: a logger is only disposed
        // once its replacement is already the static one.
        var replacedByThisSwitcher = Interlocked.Exchange(ref _installedLogger, rebuilt);
        replacedByThisSwitcher?.Dispose();
    }

    /// <summary>
    /// Moves the outgoing active file into the archive for today, so the lines
    /// it already holds are kept and retention can prune them later.
    /// </summary>
    /// <remarks>
    /// Windows refuses to move or delete a file another process still holds
    /// open, and other Eling processes (each MCP host keeps a
    /// <see cref="RollingDailyFileSink"/>) hold the same active file. A plain
    /// <see cref="File.Move(string, string)"/> therefore fails there with
    /// "used by another process", and failing silently left a large active
    /// file that belongs to no archive and is never pruned.
    /// <para>
    /// So the archive is always produced by copying the bytes out, and the
    /// active file is only removed when the delete succeeds. When it does not,
    /// the truncate below still gives the next switch a clean file whose lines
    /// already live in the archive.
    /// </para>
    /// </remarks>
    private void ArchiveActiveFile(string activePath)
    {
        byte[] content;
        try
        {
            if (!File.Exists(activePath))
            {
                return;
            }

            content = ReadAllBytes(activePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the outgoing log file {Path} for archiving.", activePath);
            return;
        }

        if (content.Length == 0)
        {
            TryDeleteOrTruncate(activePath);
            return;
        }

        var extension = Path.GetExtension(activePath);
        var prefix = Path.GetFileNameWithoutExtension(activePath);
        var archivePath = Path.Combine(_logsDirectory, $"{prefix}-{DateTime.Today:yyyy-MM-dd}{extension}");

        try
        {
            // A second switch on the same day must keep what the first one
            // already archived, so this appends rather than overwrites.
            using (var archive = new FileStream(archivePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                archive.Write(content, 0, content.Length);
            }
        }
        catch (Exception ex)
        {
            // Nothing was copied out; keeping the active file is the only way
            // not to lose the lines, so leave it alone.
            _logger.LogWarning(ex, "Could not write the outgoing log archive {Path}.", archivePath);
            return;
        }

        TryDeleteOrTruncate(activePath);
    }

    /// <summary>
    /// Removes the now-archived active file, falling back to emptying it when
    /// another process holds the handle open.
    /// </summary>
    private void TryDeleteOrTruncate(string activePath)
    {
        try
        {
            File.Delete(activePath);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete the archived active file {Path}; emptying it instead.", activePath);
        }

        try
        {
            // Opening with no sharing excludes the writers long enough to
            // truncate; it fails while a logging process still writes to it,
            // which is fine: the lines are already in the archive.
            using var stream = new FileStream(activePath, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.SetLength(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not empty the archived active file {Path}.", activePath);
        }
    }

    private static byte[] ReadAllBytes(string path)
    {
        // Other Eling processes open the shared active file with their own
        // sharing mode, and a stricter one makes a plain read throw. Widen the
        // request progressively: Delete-sharing is what a move/delete needs
        // anyway, and ReadWrite-sharing is what a concurrent writer allows.
        var shares = new[] { FileShare.ReadWrite | FileShare.Delete, FileShare.ReadWrite, FileShare.Read };

        IOException? lastFailure = null;
        foreach (var share in shares)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (IOException ex)
            {
                lastFailure = ex;
            }
        }

        throw lastFailure!;
    }

    private static string ActiveFileNameFor(LogFormat format) =>
        format == LogFormat.Jsonl ? $"{ArchivePrefix}.jsonl" : $"{ArchivePrefix}.log";
}
