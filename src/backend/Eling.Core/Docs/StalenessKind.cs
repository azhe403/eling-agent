namespace Eling.Core.Docs;

/// <summary>
/// Kind of staleness finding.
/// </summary>
public enum StalenessKind
{
    UnresolvedReference,
    SupersededWithoutReplacement,
    StaleDraft,
    Untouched
}