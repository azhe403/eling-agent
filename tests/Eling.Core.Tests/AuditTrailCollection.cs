namespace Eling.Core.Tests;

/// <summary>
/// Groups the audit-trail test classes so they never run at the same time.
/// <see cref="BufferedAuditLoggerTests"/> measures how many of 4128 entries a
/// backed-up channel drain lands inside its five-second budget, and every
/// <see cref="AuditFileStore"/> in the process appends through one shared write
/// gate — so a concurrent audit test class can tip that measurement over its
/// budget and fail a drain that actually worked. One collection makes the
/// measurement honest; see <see cref="ElingDataDirCollection"/> in the backend
/// suite for the same pattern applied to a process-global setting.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AuditTrailCollection
{
    public const string Name = "AuditTrail";
}
