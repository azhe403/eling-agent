namespace Eling.Core.Skill;

/// <summary>
/// Fire-and-forget change notification for the skill catalog (mirrors
/// <see cref="Eling.Core.Memory.IMemoryChangeNotifier"/>). Implementations
/// kick off invalidation/refresh of derived views (none in v1 — the skills
/// page reads through the REST layer); they never block the mutation.
/// </summary>
public interface ISkillChangeNotifier
{
    Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default);
}
