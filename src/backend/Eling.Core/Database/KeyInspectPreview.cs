namespace Eling.Core.Database;

public sealed record KeyInspectPreview(
    string Kind,
    IReadOnlyList<KeyInspectEntry> Entries,
    bool Truncated);