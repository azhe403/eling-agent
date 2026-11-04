namespace Eling.Core.Database;

/// <summary>
/// Which way a list runs relative to a column's own headline order.
/// </summary>
/// <remarks>
/// <para>
/// A column has a natural order - a name runs A to Z, a date runs newest
/// first - and that is what <see cref="Ascending"/> shows. The name is not
/// "ascending" on purpose: "ascending" means different things per column
/// (an ascending date would be oldest first), and what the reader controls is
/// a mirror of the column, not a universal direction.
/// </para>
/// <para>
/// <see cref="Descending"/> mirrors the column. It does not reverse the whole
/// list: the tie-break that separates equal readings stays in one direction,
/// so the reversed list is the mirror of the column and not the mirror of the
/// forward list.
/// </para>
/// </remarks>
public enum SortOrder
{
    /// <summary>The column's natural order.</summary>
    Ascending = 0,

    /// <summary>The mirror of the column's natural order.</summary>
    Descending = 1
}
