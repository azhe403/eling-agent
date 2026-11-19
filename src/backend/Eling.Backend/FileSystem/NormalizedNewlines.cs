namespace Eling.Backend.FileSystem;

/// <summary>Normalized (\n-only) text plus the index map back to the original string.</summary>
internal sealed record NormalizedNewlines(
    string Normalized,
    List<int> Map);
