namespace Eling.Core.Exceptions;

/// <summary>Write/move target already exists and overwrite was not requested.</summary>
public class PathAlreadyExistsException(string path)
    : IOException($"Path '{path}' already exists and overwrite was not requested.")
{
    public string Path { get; } = path;
}
