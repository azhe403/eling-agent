using Eling.Core;
using Eling.Core.Projects;
using Eling.Core.Scope;
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
    public bool Disposed { get; private set; }

    private AppServices(
        RuntimeRegistry registry,
        MemoryChangeBroadcaster broadcaster,
        ILoggerFactory loggerFactory,
        UserScope userScope,
        ProjectScope projectScope,
        string effectiveDataDir,
        IWorkspacesRegistry workspaces
    )
    {
        Registry = registry;
        Broadcaster = broadcaster;
        LoggerFactory = loggerFactory;
        UserScope = userScope;
        ProjectScope = projectScope;
        EffectiveDataDir = effectiveDataDir;
        Workspaces = workspaces;
    }

    public static AppServices Create(ProjectContext context)
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

        return new AppServices(
            registry,
            broadcaster,
            minimalLoggerFactory,
            context.UserScope,
            context.ProjectScope,
            context.EffectiveDataDir,
            workspaces);
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        Registry.Dispose();
        (LoggerFactory as IDisposable)?.Dispose();
    }
}
