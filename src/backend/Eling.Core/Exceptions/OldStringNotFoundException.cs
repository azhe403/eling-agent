namespace Eling.Core.Exceptions;

/// <summary><c>file_edit</c> when oldString matches nowhere in the file.</summary>
public sealed class OldStringNotFoundException(string path)
    : IOException($"Old string was not found in '{path}'.")
{
    public string Path { get; } = path;
}
