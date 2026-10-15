namespace Eling.Core.Audit.Retention;

public static class AuditRetentionPolicy
{
    public static bool IsExpired(AuditTrail trail, DateTimeOffset fileDate, DateTimeOffset now, AuditOptions options)
    {
        var days = trail == AuditTrail.Change ? options.ChangeRetentionDays : options.AccessRetentionDays;
        return now - fileDate > TimeSpan.FromDays(days);
    }
}
