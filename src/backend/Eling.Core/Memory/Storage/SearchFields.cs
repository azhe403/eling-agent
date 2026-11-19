namespace Eling.Core.Memory.Storage;

/// <summary>Pre-joined tag/source text for FTS5 indexing (tags joined with a single space).</summary>
internal sealed record SearchFields(
    string Content,
    string Tags,
    string? Source);
