using Eling.Core.Skill;

namespace Eling.Backend.Services;

/// <summary>
/// Owner-side in-proc ISkillChangeNotifier: skill mutations on this process
/// push the "skills" topic straight to SSE subscribers. Mirrors how the
/// shared MemoryChangeBroadcaster serves IMemoryChangeNotifier on the owner.
/// </summary>
public sealed class BroadcasterSkillChangeNotifier(MemoryChangeBroadcaster broadcaster) : ISkillChangeNotifier
{
    public Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default)
    {
        broadcaster.Notify(SkillChangeTopics.Skills);
        return Task.CompletedTask;
    }
}
