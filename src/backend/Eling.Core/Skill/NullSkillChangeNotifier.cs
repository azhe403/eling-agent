namespace Eling.Core.Skill;

/// <summary>No-op notifier for hosts that have nothing derived to refresh (and for tests).</summary>
public sealed class NullSkillChangeNotifier : ISkillChangeNotifier
{
    public static readonly NullSkillChangeNotifier Instance = new();

    public Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
