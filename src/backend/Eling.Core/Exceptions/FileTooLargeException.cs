namespace Eling.Core.Exceptions;

/// <summary><c>file_read</c> against a file larger than the byte cap.</summary>
public sealed class FileTooLargeException(string path, long sizeBytes, int maxBytes)
    : IOException($"File '{path}' is {sizeBytes} bytes, exceeding the {maxBytes} byte cap.")
{
    public string Path { get; } = path;
    public long SizeBytes { get; } = sizeBytes;
    public int MaxBytes { get; } = maxBytes;
}
