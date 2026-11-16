namespace Eling.Core.Skill;

/// <summary>
/// Result of <see cref="ISkillService.ListAsync"/>: the requested page
/// <see cref="Items"/>, the size of the request's selection (trash mode
/// honored, origin filter ignored; <see cref="Total"/>), and the number of
/// rows that passed the request's filter before the page window was applied
/// (<see cref="Matching"/>).
/// </summary>
public sealed record SkillListResult(
    IReadOnlyList<Skill> Items,
    int Total,
    int Matching)
{
    // Total honors the includeTrashed selection and ignores the origin
    // filter; Matching is the post-filter count. With filters absent,
    // Total == Matching.
    // Tuple-style deconstruction for existing call sites; the wire shape is
    // this record, never a ValueTuple.
    public void Deconstruct(
        out IReadOnlyList<Skill> items,
        out int total,
        out int matching)
    {
        items = Items;
        total = Total;
        matching = Matching;
    }
}
