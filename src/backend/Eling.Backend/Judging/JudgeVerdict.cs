namespace Eling.Backend.Judging;

/// <summary>
/// The judge's structured answer. Kept separate from
/// <see cref="Eling.Core.Memory.SemanticJudgement"/> because this is a wire shape: a label
/// the model picked from a fixed set rather than the domain enum, and a candidate label
/// rather than a ULID.
/// </summary>
public sealed record JudgeVerdict
{
    /// <summary>One of the relation labels offered in the prompt.</summary>
    public string? Relation { get; init; }

    /// <summary>Confidence in [0, 1]. Clamped on the way in.</summary>
    public double? Confidence { get; init; }

    /// <summary>One sentence, surfaced in the save response.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Label of the candidate the verdict applies to, for example "C1". Labels are used
    /// instead of ULIDs because a model asked to echo a 26-character identifier is far
    /// less reliable than one asked to choose from a short list.
    /// </summary>
    public string? Target { get; init; }
}
