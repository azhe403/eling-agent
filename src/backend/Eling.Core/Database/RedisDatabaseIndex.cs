namespace Eling.Core.Database;

/// <summary>
/// A Redis logical database index.
///
/// <para>
/// Redis numbers its logical databases 0 to 15, and the number reaches this
/// application from a query string or an agent's tool argument - both
/// untrusted. Carrying it as a bare <c>int</c> meant every call site had to
/// remember the range, and most did not: the endpoints handed the number
/// straight to the client, where an index outside the range failed with the
/// server's own message instead of a refusal the caller can act on.
/// </para>
/// <para>
/// This type can only hold a number inside the range, so a caller either has a
/// usable index or a refusal to return.
/// </para>
/// </summary>
public readonly record struct RedisDatabaseIndex
{
    /// <summary>The lowest index Redis numbers.</summary>
    public const int Min = 0;

    /// <summary>The highest index Redis numbers.</summary>
    public const int Max = 15;

    private RedisDatabaseIndex(int value) => Value = value;

    /// <summary>The index, always between <see cref="Min"/> and <see cref="Max"/>.</summary>
    public int Value { get; }

    /// <summary>
    /// Reads an index, or returns false when the number is outside the range
    /// Redis numbers.
    /// </summary>
    public static bool TryCreate(int value, out RedisDatabaseIndex index)
    {
        if (value < Min || value > Max)
        {
            index = default;
            return false;
        }

        index = new RedisDatabaseIndex(value);
        return true;
    }

    /// <summary>
    /// Reads an index from text - a query string or a stored field - or returns
    /// false when it is not a number inside the range.
    /// </summary>
    public static bool TryParse(string? text, out RedisDatabaseIndex index)
    {
        if (!int.TryParse(text, out var value))
        {
            index = default;
            return false;
        }

        return TryCreate(value, out index);
    }

    /// <summary>What a caller is told when the number names no database.</summary>
    public static string Refusal => $"database must be an integer between {Min} and {Max}.";

    public override string ToString() => Value.ToString();
}
