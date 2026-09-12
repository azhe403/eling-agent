namespace Eling.Core.Exceptions;

/// <summary><c>file_edit</c> when oldString matches multiple times without replaceAll.</summary>
public sealed class AmbiguousMatchException(string path, int matchCount)
    : IOException($"Old string matches {matchCount} times in '{path}'; provide more context or use replaceAll.")
{
    public string Path { get; } = path;
    public int MatchCount { get; } = matchCount;
}
