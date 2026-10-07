namespace Eling.Core.Memory;

/// <summary>
/// An <see cref="ISemanticJudge"/> verdict about one incoming memory and the active
/// memories it resembles.
/// </summary>
/// <param name="Relation">How the incoming memory relates to <paramref name="TargetId"/>.</param>
/// <param name="Confidence">Judge-reported confidence in <paramref name="Relation"/>, in [0, 1].</param>
/// <param name="Reason">One sentence explaining the verdict, surfaced in the save response.</param>
/// <param name="TargetId">
/// The candidate the verdict applies to, or <c>null</c> when <paramref name="Relation"/>
/// is <see cref="SemanticRelation.Unrelated"/> or the judge named no candidate.
/// </param>
public readonly record struct SemanticJudgement(
    SemanticRelation Relation,
    double Confidence,
    string Reason,
    MemoryId? TargetId = null)
{
    /// <summary>True when the verdict names an existing memory to merge into.</summary>
    public bool HasTarget => TargetId is not null && Relation != SemanticRelation.Unrelated;
}
