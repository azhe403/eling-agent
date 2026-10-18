namespace Eling.Core.Database;

public sealed record SqlClassification(
    SqlVerdict Verdict,
    string? Reason);