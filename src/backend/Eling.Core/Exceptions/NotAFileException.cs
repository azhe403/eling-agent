namespace Eling.Core.Exceptions;

/// <summary><c>file_read</c>/<c>file_write</c> against a directory path.</summary>
public sealed class NotAFileException(string path)
    : IOException($"Path '{path}' is not a file.")
{
    public string Path { get; } = path;
}
