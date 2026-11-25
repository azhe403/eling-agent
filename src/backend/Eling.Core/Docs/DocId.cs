namespace Eling.Core.Docs;

/// <summary>
/// Opaque document identifier. A ULID, shaped like MemoryId — never derived
/// from a path or hash. Path is the dedupe key in workspace metadata.
/// </summary>
public readonly record struct DocId
{
    public string Value { get; }

    public DocId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.ToLowerInvariant();
    }

    public static DocId NewId() => new(Ulid.NewUlid().ToString().ToLowerInvariant());

    public static DocId Parse(string value) => new(value);

    public static bool TryParse(string? value, out DocId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length != 26) return false;
        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')))
                return false;
        }
        id = new DocId(value);
        return true;
    }

    public static DocId Empty => new("00000000000000000000000000");

    public override string ToString() => Value;
}
