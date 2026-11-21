namespace Eling.Core.Docs;

/// <summary>
/// Policy entry.
/// </summary>
public sealed record DocPolicy(
    string Key,
    string Value,
    DateTimeOffset UpdatedAt);