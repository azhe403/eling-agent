namespace Eling.Core.Audit;

public sealed record AuditActor(string Actor, string Source)
{
    public static readonly AuditActor Unknown = new("unknown", "system");
    public static readonly AuditActor System = new("system", "system");
}
