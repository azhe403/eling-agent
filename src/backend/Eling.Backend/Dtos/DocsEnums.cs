namespace Eling.Backend.Dtos;

/// <summary>
/// Coverage policy DTO.
/// </summary>
public sealed record CoveragePolicyDto(
    string[] Include,
    string[] Exclude);

/// <summary>
/// Document kind enum DTO.
/// </summary>
public enum DocKindDto { Spec = 0, Plan = 1, Doc = 2, Scratch = 3 }

/// <summary>
/// Reference kind enum DTO.
/// </summary>
public enum ReferenceKindDto { Symbol = 0, Path = 1, AnchorLink = 2 }

/// <summary>
/// Relation kind enum DTO.
/// </summary>
public enum RelationKindDto { Implements = 0, Supersedes = 1, References = 2 }

/// <summary>
/// Staleness kind enum DTO.
/// </summary>
public enum StalenessKindDto { UnresolvedReference = 0, SupersededWithoutReplacement = 1, StaleDraft = 2, Untouched = 3 }
