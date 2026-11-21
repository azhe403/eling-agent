namespace Eling.Core.Docs;

/// <summary>Kind of relation between documents.</summary>
public enum RelationKind
{
    /// <summary>A plan implements a spec.</summary>
    Implements = 0,

    /// <summary>A document supersedes another.</summary>
    Supersedes = 1,

    /// <summary>A generic reference.</summary>
    References = 2,
}