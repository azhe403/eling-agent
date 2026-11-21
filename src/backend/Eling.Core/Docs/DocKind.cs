namespace Eling.Core.Docs;

/// <summary>
/// Kind of document. Determines the status vocabulary and default behaviour.
/// </summary>
public enum DocKind
{
    /// <summary>A specification with an approval lifecycle.</summary>
    Spec = 0,

    /// <summary>A plan with step progress from checkboxes.</summary>
    Plan = 1,

    /// <summary>A standalone document without special lifecycle.</summary>
    Doc = 2,

    /// <summary>An agent work-product, archived by default.</summary>
    Scratch = 3,
}