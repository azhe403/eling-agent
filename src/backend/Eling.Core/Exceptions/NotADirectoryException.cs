namespace Eling.Core.Exceptions;

/// <summary><c>directory_list</c> against a path that is not a directory.</summary>
public sealed class NotADirectoryException(string path)
    : IOException($"Path '{path}' is not a directory.")
{
    public string Path { get; } = path;
}
