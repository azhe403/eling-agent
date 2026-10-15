using Eling.Backend.Services;
using Eling.Core.Projects;
using Eling.Core.Runtime;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The sampler is the only thing that turns live runtimes into metric
/// batches, so its contract with RuntimeRegistry and ProcessMetricsTracker is
/// what every SSE frame is made of.
/// </summary>
public sealed class RuntimeMetricsSamplerTests : IDisposable
{
    private readonly string _root;

    public RuntimeMetricsSamplerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-metrics-" + Guid.NewGuid().ToString("N")[..8]);
    }

    private RuntimeRegistry NewRegistry()
    {
        Directory.CreateDirectory(_root);
        var dataDir = Path.Combine(_root, "data");
        var userScope = UserScope.Resolve(Path.Combine(dataDir, "user-scope"));
        var workspaces = new JsonWorkspaceRegistry(Path.Combine(dataDir, "agent-workspaces.json"));
        return new RuntimeRegistry(NullLogger<RuntimeRegistry>.Instance, userScope, workspaces: workspaces);
    }

    private static RuntimeMetricsSampler NewSampler(RuntimeRegistry registry) =>
        new(registry, new ProcessMetricsTracker(NullLogger<ProcessMetricsTracker>.Instance), null);

    private void Register(RuntimeRegistry registry, int processId)
    {
        var workspace = Path.Combine(_root, $"ws-{processId}");
        Directory.CreateDirectory(workspace);
        registry.Register(new RuntimeRegistration
        {
            ProcessId = processId,
            HeadScopeRoot = workspace,
            WorkspaceRoot = workspace,
            CodebaseEnabled = true,
            DataDirectory = Path.Combine(workspace, ".eling"),
            StartTime = DateTimeOffset.UtcNow,
            McpEnabled = true,
            McpTransport = "stdio",
        });
    }

    [Fact]
    public void SampleAll_OwnPidRegistered_ReturnsWorkingSetMetrics()
    {
        using var registry = NewRegistry();
        Register(registry, Environment.ProcessId);

        var snapshots = NewSampler(registry).SampleAll();

        var own = Assert.Single(snapshots);
        Assert.Equal(Environment.ProcessId, own.Pid);
        Assert.True(own.MemoryBytes > 0);
        Assert.InRange(own.CpuPercent, 0, 100);
        Assert.NotEqual(default, own.SampledAt);
    }

    [Fact]
    public void SampleAll_LiveAndDeadPids_OmitsTheDeadOne()
    {
        using var registry = NewRegistry();
        Register(registry, Environment.ProcessId);
        Register(registry, int.MaxValue);

        var snapshots = NewSampler(registry).SampleAll();

        var own = Assert.Single(snapshots);
        Assert.Equal(Environment.ProcessId, own.Pid);
    }

    [Fact]
    public void SampleAll_CalledTwiceApart_KeepsCpuWithinRange()
    {
        using var registry = NewRegistry();
        Register(registry, Environment.ProcessId);
        var sampler = NewSampler(registry);

        var first = sampler.SampleAll();
        Thread.Sleep(300);
        var second = sampler.SampleAll();

        Assert.Contains(first, s => s.Pid == Environment.ProcessId);
        Assert.Contains(second, s => s.Pid == Environment.ProcessId);
        Assert.All(second, s => Assert.InRange(s.CpuPercent, 0, 100));
    }

    [Fact]
    public void SampleAll_NoRuntimes_ReturnsEmptyBatch()
    {
        using var registry = NewRegistry();

        Assert.Empty(NewSampler(registry).SampleAll());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            /* best effort: temp dir cleanup must not fail a test */
        }

        GC.SuppressFinalize(this);
    }
}
