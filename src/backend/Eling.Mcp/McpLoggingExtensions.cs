using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace Eling.Mcp;

public static class McpLoggingExtensions
{
    private const string DefaultRootPath = ".eling";

    public static IServiceCollection AddElingLogging(this IServiceCollection services, string rootPath = DefaultRootPath)
    {
        var logsDirectory = Path.Combine(rootPath, "logs");
        Directory.CreateDirectory(logsDirectory);

        var sink = new RollingDailyFileSink(logsDirectory);
        services.AddSingleton(sink);
        services.AddHostedService(sp => new DailyLogRollerService(logsDirectory, sp.GetService<RollingDailyFileSink>()));

        return services.AddSerilog((_, lc) => lc
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose)
            .WriteTo.Sink(sink));
    }
}

public sealed class RollingDailyFileSink : ILogEventSink, IDisposable
{
    private const string ActiveFileName = "mcp.log";
    private const int DefaultRetainedDays = 7;
    private const string LogFilePattern = "mcp-*.log";
    private const string OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

    private readonly string _logsDirectory;
    private readonly string _activeFileName;
    private readonly int _retainedDays;
    private readonly ITextFormatter _formatter;
    private readonly object _syncRoot = new();

    private DateTime _currentDate;
    private FileStream? _fileStream;
    private StreamWriter? _writer;
    private bool _disposed;

    private static readonly Regex TimestampRegex = new(@"^\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);

    public RollingDailyFileSink(
        string logsDirectory,
        string activeFileName = ActiveFileName,
        int retainedDays = DefaultRetainedDays,
        ITextFormatter? formatter = null
    )
    {
        _logsDirectory = logsDirectory;
        _activeFileName = activeFileName;
        _retainedDays = retainedDays;
        _formatter = formatter ?? new MessageTemplateTextFormatter(OutputTemplate);
        _currentDate = DateTime.Today;

        Directory.CreateDirectory(_logsDirectory);
        PerformRolloverWithCrossProcessLock(_currentDate);
        EnsureActiveStreamOpen();
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed) return;

        lock (_syncRoot)
        {
            var eventDate = logEvent.Timestamp.LocalDateTime.Date;
            if (eventDate != _currentDate)
            {
                Rollover(eventDate);
            }

            if (_writer == null)
            {
                EnsureActiveStreamOpen();
            }

            if (_writer != null)
            {
                _formatter.Format(logEvent, _writer);
                _writer.Flush();
            }
        }
    }

    public void CheckRollover(DateTime targetDate)
    {
        lock (_syncRoot)
        {
            if (targetDate != _currentDate)
            {
                Rollover(targetDate);
            }
            else
            {
                PerformRolloverWithCrossProcessLock(targetDate);
            }
        }
    }

    private void Rollover(DateTime newDate)
    {
        CloseActiveStream();
        PerformRolloverWithCrossProcessLock(newDate);
        _currentDate = newDate;
        EnsureActiveStreamOpen();
    }

    private void EnsureActiveStreamOpen()
    {
        if (_writer != null || _disposed) return;

        var activePath = Path.Combine(_logsDirectory, _activeFileName);
        try
        {
            _fileStream = new FileStream(activePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(_fileStream, Encoding.UTF8) { AutoFlush = true };
        }
        catch
        {
            // If file access is temporarily locked, writer stays null and will retry on next emit
        }
    }

    private void CloseActiveStream()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch { }
        finally
        {
            _writer = null;
        }

        try
        {
            _fileStream?.Dispose();
        }
        catch { }
        finally
        {
            _fileStream = null;
        }
    }

    private void PerformRolloverWithCrossProcessLock(DateTime today)
    {
        var mutexName = "Local\\Eling_Log_Rollover_" + GetDirectoryHash(_logsDirectory);
        Mutex? mutex = null;
        var acquired = false;

        try
        {
            mutex = new Mutex(false, mutexName);
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            RotateAndPruneLogsUnderLock(today);
        }
        catch
        {
            // Best-effort in case of OS mutex restrictions
            RotateAndPruneLogsUnderLock(today);
        }
        finally
        {
            if (acquired && mutex != null)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
            mutex?.Dispose();
        }
    }

    private void RotateAndPruneLogsUnderLock(DateTime today)
    {
        var activePath = Path.Combine(_logsDirectory, _activeFileName);
        if (File.Exists(activePath))
        {
            TryRotateActiveLog(activePath, today);
        }

        PruneOldLogs(today);
    }

    private void TryRotateActiveLog(string activePath, DateTime today)
    {
        try
        {
            // Read lines with FileShare.ReadWrite to allow concurrent readers/writers
            var lines = new List<string>();
            using (var stream = new FileStream(activePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                }
            }

            if (lines.Count == 0) return;

            var priorDayGroups = new Dictionary<string, List<string>>();
            var todayLines = new List<string>();
            string? currentLineDate = null;

            foreach (var line in lines)
            {
                var match = TimestampRegex.Match(line);
                if (match.Success)
                {
                    currentLineDate = match.Value;
                }

                if (currentLineDate != null && DateTime.TryParse(currentLineDate, out var parsedDate) && parsedDate.Date < today.Date)
                {
                    var dateKey = parsedDate.Date.ToString("yyyy-MM-dd");
                    if (!priorDayGroups.TryGetValue(dateKey, out var group))
                    {
                        group = new List<string>();
                        priorDayGroups[dateKey] = group;
                    }
                    group.Add(line);
                }
                else
                {
                    todayLines.Add(line);
                }
            }

            if (priorDayGroups.Count > 0)
            {
                // Write prior day lines to their respective archive files
                foreach (var (dateKey, groupLines) in priorDayGroups)
                {
                    var archivePath = Path.Combine(_logsDirectory, $"mcp-{dateKey}.log");
                    File.AppendAllLines(archivePath, groupLines, Encoding.UTF8);
                }

                // Rewrite active file with today's lines (or truncate if empty)
                using (var stream = new FileStream(activePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    foreach (var line in todayLines)
                    {
                        writer.WriteLine(line);
                    }
                }
            }
        }
        catch
        {
            // In case of transient file locking by another process, will retry on next interval or emit
        }
    }

    private void PruneOldLogs(DateTime today)
    {
        try
        {
            if (!Directory.Exists(_logsDirectory)) return;

            var cutoff = today.Date.AddDays(-_retainedDays);
            foreach (var filePath in Directory.GetFiles(_logsDirectory, "mcp-*.log"))
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath); // e.g. "mcp-2026-08-13"
                if (fileName.StartsWith("mcp-", StringComparison.OrdinalIgnoreCase))
                {
                    var datePart = fileName[4..];
                    if (DateTime.TryParse(datePart, out var logDate))
                    {
                        if (logDate.Date < cutoff)
                        {
                            try { File.Delete(filePath); } catch { }
                        }
                    }
                    else if (File.GetLastWriteTime(filePath).Date < cutoff)
                    {
                        try { File.Delete(filePath); } catch { }
                    }
                }
            }
        }
        catch { }
    }

    private static string GetDirectoryHash(string directory)
    {
        var fullPath = Path.GetFullPath(directory).ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath));
        return Convert.ToHexString(bytes)[..16];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_syncRoot)
        {
            CloseActiveStream();
        }
    }
}

public sealed class DailyLogRollerService : BackgroundService
{
    private readonly string _logsDirectory;
    private readonly RollingDailyFileSink? _sink;

    public DailyLogRollerService(string logsDirectory, RollingDailyFileSink? sink = null)
    {
        _logsDirectory = logsDirectory;
        _sink = sink;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var nextMidnight = now.Date.AddDays(1).AddSeconds(1);
            var delay = nextMidnight - now;

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }

            _sink?.CheckRollover(DateTime.Today);
        }
    }
}
