using System.Text.RegularExpressions;

namespace Eling.Core;

public class Memory
{
    private static readonly Regex TagSeparatorRegex = new(@"[\s\-_./\\|]+", RegexOptions.Compiled);

    public MemoryId Id { get; }
    public MemoryType Type { get; }
    public MemoryStatus Status { get; set; }
    public string Content { get; set; }
    public IReadOnlyCollection<string> Tags { get; set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Source { get; set; }

    public Memory(
        MemoryType type,
        string content,
        IEnumerable<string>? tags = null,
        string? source = null,
        MemoryStatus status = MemoryStatus.Active,
        MemoryId? id = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        Id = id ?? MemoryId.NewId();
        Type = type;
        Status = status;
        Content = content;
        Tags = NormalizeTags(tags);
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        UpdatedAt = updatedAt ?? CreatedAt;
        Source = source;
    }

    /// <summary>
    /// Normalize a tag list for indexability: split on common separators
    /// (hyphen, underscore, dot, slash, backslash, pipe), lower-case, trim,
    /// drop tokens shorter than 2 characters and remove duplicates. Used by
    /// the constructor, the indexer, and the search-side re-normalizer.
    /// </summary>
    public static IReadOnlyCollection<string> NormalizeTags(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tags)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var parts = TagSeparatorRegex.Split(raw.Trim().ToLowerInvariant());
            foreach (var part in parts)
            {
                var token = part.Trim();
                if (token.Length < 2)
                {
                    continue;
                }
                if (seen.Add(token))
                {
                    tokens.Add(token);
                }
            }
        }
        return tokens.AsReadOnly();
    }
}
