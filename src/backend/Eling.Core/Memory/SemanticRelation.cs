namespace Eling.Core.Memory;

/// <summary>
/// The semantic relationship between an incoming memory and an existing one, as
/// judged by an <see cref="ISemanticJudge"/>. Distinct from the heuristic score in
/// <see cref="MemorySimilarity"/>, which measures shared vocabulary and cannot
/// express any of the non-identical cases below.
/// </summary>
public enum SemanticRelation
{
    /// <summary>Different topics; the incoming memory should stand on its own.</summary>
    Unrelated,

    /// <summary>Same meaning and same guidance.</summary>
    Identical,

    /// <summary>Same topic and intent, with the incoming memory refining or correcting the existing one.</summary>
    Revision,

    /// <summary>Conflicting guidance on the same topic, so the incoming memory supersedes the existing one.</summary>
    Contradiction,

    /// <summary>The incoming memory contains everything the existing one says, plus more.</summary>
    Superset,

    /// <summary>The incoming memory is a narrower version of the existing one.</summary>
    Subset,
}
