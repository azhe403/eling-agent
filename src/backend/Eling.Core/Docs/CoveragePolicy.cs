namespace Eling.Core.Docs;

/// <summary>
/// Coverage policy for document scanning.
/// </summary>
public sealed record CoveragePolicy
{
    public string[] Include { get; set; } = Array.Empty<string>();
    public string[] Exclude { get; set; } = Array.Empty<string>();
}