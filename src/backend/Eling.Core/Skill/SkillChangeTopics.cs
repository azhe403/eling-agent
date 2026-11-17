namespace Eling.Core.Skill;

/// <summary>
/// The skill-catalog change topic on the shared change stream
/// (/api/events/memories). Single constant so the owner's in-proc adapter,
/// the MCP host's HTTP relay, the catalog watcher and the frontend all agree
/// on the wire value.
/// </summary>
public static class SkillChangeTopics
{
    public const string Skills = "skills";
}
