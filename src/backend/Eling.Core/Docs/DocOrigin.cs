namespace Eling.Core.Docs;

/// <summary>
/// Origin of a document — where it was found. This is separate from ownership
/// because a document in the current repo's <c>docs/</c> is <see cref="External"/>
/// even though it sits in the workspace the user opened.
/// </summary>
public enum DocOrigin
{
    /// <summary>The current repository workspace.</summary>
    Current = 0,

    /// <summary>An ancestor <c>.eling</c> scope visible from a sub-folder workspace.</summary>
    Ancestor = 1,

    /// <summary>The machine-local project shard under <c>DATA/eling/projects/<name>-<hash>/docs/</c>.</summary>
    Shard = 2,

    /// <summary>The global configuration directory <c>CONFIG/eling/docs/</c>.</summary>
    Global = 3,
}