using System;

namespace Eling.Core.Database;

/// <summary>
/// The order the connection list is drawn in.
///
/// <para>
/// The registry hands the rows back in the order the connections happened to
/// be registered, which is not an answer to any question a reader has. Four
/// keys are offered instead - name, provider, created, last used - and the
/// server applies the order, so every client draws the same list.
/// </para>
/// <para>
/// Three rules make the order honest, and they are the ones that are easy to
/// get wrong:
/// </para>
/// <list type="bullet">
/// <item>
/// A row the server never reported a reading for - a connection that has
/// never been connected - stays at the bottom in <em>both</em> directions.
/// "No reading" is unknown, not zero, and an unknown value never claims the
/// top of a list.
/// </item>
/// <item>
/// Equal readings fall back to the name, and the name tie-break runs A to Z
/// whichever way the column runs. The order therefore never depends on the
/// order the rows happened to be registered in.
/// </item>
/// <item>
/// <see cref="SortOrder.Descending"/> mirrors the column, not the list. The
/// name tie-break does not flip with it.
/// </item>
/// </list>
/// </summary>
public static class DatabaseConnectionSort
{
    /// <summary>The key used when the caller names none, or names one that is not known.</summary>
    public const string DefaultKey = "name";

    /// <summary>The connection name.</summary>
    public const string NameKey = "name";

    /// <summary>The provider the connection speaks.</summary>
    public const string ProviderKey = "provider";

    /// <summary>The moment the connection was registered.</summary>
    public const string CreatedKey = "created";

    /// <summary>The moment the connection was last reached.</summary>
    public const string LastUsedKey = "lastUsed";

    /// <summary>
    /// Reads a sort key off the wire.
    /// </summary>
    /// <param name="value">The wire value, case-insensitive.</param>
    /// <param name="key">The canonical key, or <see cref="DefaultKey"/> when the value names no known key.</param>
    /// <returns>True when <paramref name="value"/> names a known key.</returns>
    /// <remarks>
    /// An unknown key is not an error. A list endpoint that refuses a sort
    /// parameter answers 400 for a request a reader made by typing a link
    /// with a typo in it; falling back to the default answers the question
    /// they actually asked.
    /// </remarks>
    public static bool TryParseKey(string? value, out string key)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case NameKey:
                key = NameKey;
                return true;
            case ProviderKey:
                key = ProviderKey;
                return true;
            case CreatedKey:
                key = CreatedKey;
                return true;
            case "lastused":
                key = LastUsedKey;
                return true;
            default:
                key = DefaultKey;
                return false;
        }
    }

    /// <summary>
    /// Reads a sort direction off the wire.
    /// </summary>
    /// <param name="value">The wire value, case-insensitive.</param>
    /// <param name="order">The direction, or <see cref="SortOrder.Ascending"/> when the value names no known direction.</param>
    /// <returns>True when <paramref name="value"/> names a known direction.</returns>
    public static bool TryParseOrder(string? value, out SortOrder order)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "asc":
            case "ascending":
                order = SortOrder.Ascending;
                return true;
            case "desc":
            case "descending":
                order = SortOrder.Descending;
                return true;
            default:
                order = SortOrder.Ascending;
                return false;
        }
    }

    /// <summary>
    /// The same connections, in the requested order.
    /// </summary>
    /// <param name="rows">The connections to order.</param>
    /// <param name="key">The column to order by; an unknown key orders by name.</param>
    /// <param name="order">The direction the column runs in.</param>
    /// <returns>A new list holding the same connections in the requested order.</returns>
    /// <remarks>
    /// The caller's list is never mutated: the caller owns it, and a sort that
    /// reordered it in place would change every other count derived from it.
    /// </remarks>
    public static IReadOnlyList<DatabaseConnection> Apply(
        IReadOnlyList<DatabaseConnection> rows,
        string key,
        SortOrder order)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var result = new List<DatabaseConnection>(rows);
        var resolved = ResolveKey(key);
        result.Sort((left, right) => Compare(left, right, resolved, order));
        return result;
    }

    /// <summary>The canonical key for a wire value; an unknown key orders by name.</summary>
    private static string ResolveKey(string? key) =>
        TryParseKey(key, out var resolved) ? resolved : DefaultKey;

    /// <summary>
    /// One step of the comparison: the column first, then the name, then the
    /// provider, then the id - so no two rows ever depend on the order they
    /// were registered in.
    /// </summary>
    private static int Compare(
        DatabaseConnection left,
        DatabaseConnection right,
        string key,
        SortOrder order)
    {
        var primary = CompareColumn(left, right, key, order);
        if (primary != 0) return primary;

        var byName = CompareNames(left.Name, right.Name);
        if (byName != 0) return byName;

        var byProvider = left.Provider.CompareTo(right.Provider);
        if (byProvider != 0) return byProvider;

        return string.CompareOrdinal(left.Id, right.Id);
    }

    /// <summary>
    /// The comparison on the chosen column alone.
    /// </summary>
    /// <remarks>
    /// The direction applies to the column only. The name that separates equal
    /// readings is compared elsewhere, and it always runs A to Z - so the
    /// descending list is the mirror of the column, not the reverse of the
    /// ascending list.
    /// </remarks>
    private static int CompareColumn(
        DatabaseConnection left,
        DatabaseConnection right,
        string key,
        SortOrder order)
    {
        switch (key)
        {
            case ProviderKey:
            {
                var byProvider = left.Provider.CompareTo(right.Provider);
                return order == SortOrder.Descending ? -byProvider : byProvider;
            }

            case CreatedKey:
            {
                var byCreated = right.CreatedAt.CompareTo(left.CreatedAt);
                return order == SortOrder.Descending ? -byCreated : byCreated;
            }

            case LastUsedKey:
            {
                // A connection that was never reached has no reading at all.
                // It sits below every row that has one, in either direction:
                // "never" is not "the oldest".
                if (left.LastConnectedAt is null && right.LastConnectedAt is null) return 0;
                if (left.LastConnectedAt is null) return 1;
                if (right.LastConnectedAt is null) return -1;

                var byLastUsed = right.LastConnectedAt.Value.CompareTo(left.LastConnectedAt.Value);
                return order == SortOrder.Descending ? -byLastUsed : byLastUsed;
            }

            default:
            {
                var byName = CompareNames(left.Name, right.Name);
                return order == SortOrder.Descending ? -byName : byName;
            }
        }
    }

    /// <summary>
    /// Compares two names the way a reader reads them: case does not matter,
    /// and a number in the name is read as a number - "Alpha2" comes before
    /// "Alpha10", not after it.
    /// </summary>
    private static int CompareNames(string left, string right)
    {
        var i = 0;
        var j = 0;

        while (i < left.Length && j < right.Length)
        {
            var leftChar = left[i];
            var rightChar = right[j];

            if (char.IsDigit(leftChar) && char.IsDigit(rightChar))
            {
                var leftStart = i;
                var rightStart = j;

                while (i < left.Length && char.IsDigit(left[i])) i++;
                while (j < right.Length && char.IsDigit(right[j])) j++;

                var leftDigits = left[leftStart..i].TrimStart('0');
                var rightDigits = right[rightStart..j].TrimStart('0');

                if (leftDigits.Length != rightDigits.Length)
                {
                    return leftDigits.Length < rightDigits.Length ? -1 : 1;
                }

                var byNumber = string.CompareOrdinal(leftDigits, rightDigits);
                if (byNumber != 0) return byNumber < 0 ? -1 : 1;

                // The numbers are equal - "02" and "2" - so the rest decides.
                continue;
            }

            var byChar = char.ToLowerInvariant(leftChar).CompareTo(char.ToLowerInvariant(rightChar));
            if (byChar != 0) return byChar;

            i++;
            j++;
        }

        // Whatever is left over is the shorter name, and the shorter name
        // comes first.
        return (left.Length - i).CompareTo(right.Length - j);
    }
}
