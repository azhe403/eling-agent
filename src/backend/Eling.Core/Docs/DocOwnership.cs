namespace Eling.Core.Docs;

/// <summary>
/// Ownership marker of a document row. Lives only in the index and in memory -
/// never in front-matter. <see cref="Managed"/> documents are created and managed
/// through Eling; <see cref="External"/> documents are owned by the repository
/// and are permanently read-only to every mutating path.
/// </summary>
public enum DocOwnership
{
    Managed = 0,
    External = 1,
}