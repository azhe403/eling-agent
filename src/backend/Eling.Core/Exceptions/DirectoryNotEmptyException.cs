namespace Eling.Core.Exceptions;

/// <summary><c>directory_delete</c> without recursive=true against a non-empty directory.</summary>
public sealed class DirectoryNotEmptyException(string path)
    : IOException($"Directory '{path}' is not empty; retry with recursive deletion.")
{
    public string Path { get; } = path;
}
