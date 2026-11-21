namespace Eling.Core.Docs;

/// <summary>
/// Document identifier encoding ownership, origin, and a SHA-1 of the relative path.
/// Format: <c>{ownership}:{origin}:{sha1(rel_path)}</c>.
/// </summary>
public readonly record struct DocId
{
    public DocOwnership Ownership { get; }
    public DocOrigin Origin { get; }
    public string PathHash { get; }

    public DocId(DocOwnership ownership, DocOrigin origin, string pathHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathHash);
        Ownership = ownership;
        Origin = origin;
        PathHash = pathHash.ToLowerInvariant();
    }

    public static DocId Create(DocOwnership ownership, DocOrigin origin, string relativePath)
    {
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var hash = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(relativePath));
        var pathHash = Convert.ToHexString(hash).ToLowerInvariant();
        return new DocId(ownership, origin, pathHash);
    }

    public string Value => $"{Ownership.ToString().ToLowerInvariant()}:{Origin.ToString().ToLowerInvariant()}:{PathHash}";

    public override string ToString() => Value;

    public static DocId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var parts = value.Split(':', 3);
        if (parts.Length != 3 ||
            !Enum.TryParse<DocOwnership>(parts[0], true, out var ownership) ||
            !Enum.TryParse<DocOrigin>(parts[1], true, out var origin))
        {
            throw new ArgumentException("Invalid DocId format.", nameof(value));
        }
        return new DocId(ownership, origin, parts[2]);
    }

    public static bool TryParse(string? value, out DocId result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            return false;
        }
        try
        {
            result = Parse(value);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }
}