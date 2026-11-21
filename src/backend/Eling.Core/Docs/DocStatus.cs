namespace Eling.Core.Docs;

/// <summary>Status for a <see cref="DocKind.Spec"/>.</summary>
public enum SpecStatus
{
    Draft = 0,
    Review = 1,
    Approved = 2,
    Implemented = 3,
    Superseded = 4,
}

/// <summary>Status for a <see cref="DocKind.Plan"/>.</summary>
public enum PlanStatus
{
    Active = 0,
    Completed = 1,
    Superseded = 2,
    Stale = 3,
}

/// <summary>Status for a <see cref="DocKind.Doc"/>.</summary>
public enum DocStatus
{
    Active = 0,
    Superseded = 1,
    Stale = 2,
}

/// <summary>Status for a <see cref="DocKind.Scratch"/>.</summary>
public enum ScratchStatus
{
    Archived = 0,
    Active = 1,
}

/// <summary>Union-like helper to carry any status without generics.</summary>
public readonly record struct DocStatusValue
{
    public DocKind Kind { get; }
    public int Value { get; }

    public DocStatusValue(DocKind kind, int value)
    {
        Kind = kind;
        Value = value;
    }

    public static DocStatusValue FromSpec(SpecStatus s) => new(DocKind.Spec, (int)s);
    public static DocStatusValue FromPlan(PlanStatus s) => new(DocKind.Plan, (int)s);
    public static DocStatusValue FromDoc(DocStatus s) => new(DocKind.Doc, (int)s);
    public static DocStatusValue FromScratch(ScratchStatus s) => new(DocKind.Scratch, (int)s);

    public bool IsSuperseded() => Kind switch
    {
        DocKind.Spec => Value == (int)SpecStatus.Superseded,
        DocKind.Plan => Value == (int)PlanStatus.Superseded,
        DocKind.Doc => Value == (int)DocStatus.Superseded,
        _ => false,
    };

    public bool IsStale() => Kind switch
    {
        DocKind.Spec => false, // specs have no stale status
        DocKind.Plan => Value == (int)PlanStatus.Stale,
        DocKind.Doc => Value == (int)DocStatus.Stale,
        DocKind.Scratch => false,
        _ => false,
    };

    public string DisplayName => Kind switch
    {
        DocKind.Spec => ((SpecStatus)Value).ToString(),
        DocKind.Plan => ((PlanStatus)Value).ToString(),
        DocKind.Doc => ((DocStatus)Value).ToString(),
        DocKind.Scratch => ((ScratchStatus)Value).ToString(),
        _ => Value.ToString(),
    };

    public static DocStatusValue DefaultFor(DocKind kind) => kind switch
    {
        DocKind.Spec => FromSpec(SpecStatus.Draft),
        DocKind.Plan => FromPlan(PlanStatus.Active),
        DocKind.Doc => FromDoc(DocStatus.Active),
        DocKind.Scratch => FromScratch(ScratchStatus.Archived),
        _ => FromDoc(DocStatus.Active),
    };
}