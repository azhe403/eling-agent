using Eling.Core;
using Eling.Core.CommitLease;
using Eling.Core.Projects;
using Eling.Core.Scope;
using Eling.Backend.Services;

using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

public sealed class AppServices : IDisposable
{
    public RuntimeRegistry Registry { get; }
    public MemoryChangeBroadcaster Broadcaster { get; }
    public ILoggerFactory LoggerFactory { get; }
    public UserScope UserScope { get; }
    public ProjectScope ProjectScope { get; }
    public string EffectiveDataDir { get; }
    public IWorkspacesRegistry Workspaces { get; }
    public ICommitLeaseStore CommitLeaseStore { get; }
    public bool Disposed { get; private set; }

    private AppServices(
        RuntimeRegistry registry,
        MemoryChangeBroadcaster broadcaster,
        ILoggerFactory loggerFactory,
        UserScope userScope,
        ProjectScope projectScope,
        string effectiveDataDir,
        IWorkspacesRegistry workspaces,
        ICommitLeaseStore leaseStore
    )
    {
        Registry = registry;
        Broadcaster = broadcaster;
        LoggerFactory = loggerFactory;
        UserScope = userScope;
        ProjectScope = projectScope;
        EffectiveDataDir = effectiveDataDir;
        Workspaces = workspaces;
        CommitLeaseStore = leaseStore;
    }

    // coordinatorPort is the loopback port the dashboard owner serves on, when the
    // caller knows it (the MCP host always does). It wires the shared store's
    // change relay to the owner exactly like the memory notifier; a caller that
    // cannot name the port (TestAppBuilder and other standalone harnesses) gets a
    // store with no notifier rather than one that fire-and-forget posts at an
    // assumed port.
    public static AppServices Create(ProjectContext context, int? coordinatorPort = null)
    {
        var minimalLoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
        {
        });
        var broadcaster = new MemoryChangeBroadcaster();
        var workspaces = new JsonWorkspaceRegistry(
            Path.Combine(CentralAgentDirectory.Resolve(), "agent-workspaces.json"),
            minimalLoggerFactory.CreateLogger<JsonWorkspaceRegistry>());
        var registry = new RuntimeRegistry(
            minimalLoggerFactory.CreateLogger<RuntimeRegistry>(),
            context.UserScope,
            broadcaster,
            workspaces);

        // Commit leases live in one machine-global file every eling process
        // reads and writes; both hosts consume this same instance (mirrors
        // Workspaces), so an in-proc mutation and a peer's mutation land on the
        // same store object. Cross-process delivery rides the file mutex plus
        // the HTTP relay attached here — the one channel a peer can reach the
        // owner through, same as the memory notifier.
        var leaseStore = new JsonCommitLeaseStore(
            Path.Combine(CentralAgentDirectory.Resolve(), "commit-leases.json"),
            coordinatorPort is int relayPort ? new HttpCoordinatorCommitLeaseChangeNotifier(relayPort) : null,
            TimeProvider.System,
            minimalLoggerFactory.CreateLogger<JsonCommitLeaseStore>());

        return new AppServices(
            registry,
            broadcaster,
            minimalLoggerFactory,
            context.UserScope,
            context.ProjectScope,
            context.EffectiveDataDir,
            workspaces,
            leaseStore);
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        Registry.Dispose();
        (LoggerFactory as IDisposable)?.Dispose();
    }
}
