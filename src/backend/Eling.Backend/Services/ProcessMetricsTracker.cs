using System.Collections.Concurrent;
using System.Diagnostics;

namespace Eling.Backend.Services;

public sealed class ProcessMetricsTracker
{
    private readonly ConcurrentDictionary<int, CpuSample> _samples = new();

    public RuntimeProcessMetrics? Sample(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return null;

            var mem = proc.WorkingSet64;
            var now = DateTimeOffset.UtcNow;
            var cpuTime = proc.TotalProcessorTime;

            double cpuPercent = 0;
            if (_samples.TryGetValue(pid, out var prev))
            {
                var elapsed = (now - prev.Timestamp).TotalSeconds;
                var spent = (cpuTime - prev.CpuTime).TotalSeconds;
                if (elapsed > 0.2)
                {
                    cpuPercent = Math.Round((spent / elapsed) / Environment.ProcessorCount * 100, 1);
                    if (cpuPercent < 0) cpuPercent = 0;
                    if (cpuPercent > 100) cpuPercent = 100;
                    _samples[pid] = new CpuSample(now, cpuTime);
                }
            }
            else
            {
                _samples[pid] = new CpuSample(now, cpuTime);
            }

            return new RuntimeProcessMetrics(mem, cpuPercent);
        }
        catch
        {
            _samples.TryRemove(pid, out _);
            return null;
        }
    }
}
