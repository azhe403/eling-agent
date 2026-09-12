namespace Eling.Core.Exceptions;

/// <summary><c>file_read</c> against a file with NUL bytes in the probe window.</summary>
public sealed class BinaryFileException(string path)
    : IOException($"File '{path}' looks binary and cannot be read as text.")
{
    public string Path { get; } = path;
}
