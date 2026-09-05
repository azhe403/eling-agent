using Eling.Core;
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
    public bool Disposed { get; private set; }

    private AppServices(
        RuntimeRegistry registry,
        MemoryChangeBroadcaster broadcaster,
        ILoggerFactory loggerFactory,
        UserScope userScope,
        ProjectScope projectScope,
        string effectiveDataDir
    )
    {
        Registry = registry;
        Broadcaster = broadcaster;
        LoggerFactory = loggerFactory;
        UserScope = userScope;
        ProjectScope = projectScope;
        EffectiveDataDir = effectiveDataDir;
    }

    public static AppServices Create(ProjectContext context)
    {
        var minimalLoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
        {
        });
        var broadcaster = new MemoryChangeBroadcaster();
        var registry = new RuntimeRegistry(
            minimalLoggerFactory.CreateLogger<RuntimeRegistry>(),
            context.UserScope,
            broadcaster);

        return new AppServices(
            registry,
            broadcaster,
            minimalLoggerFactory,
            context.UserScope,
            context.ProjectScope,
            context.EffectiveDataDir);
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        Registry.Dispose();
        (LoggerFactory as IDisposable)?.Dispose();
    }
}
