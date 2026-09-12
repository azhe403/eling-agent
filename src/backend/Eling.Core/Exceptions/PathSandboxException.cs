namespace Eling.Core.Exceptions;

/// <summary>Resolved path escapes the configured sandbox root.</summary>
public sealed class PathSandboxException(string path, string root)
    : IOException($"Path '{path}' is outside the sandbox root '{root}'.")
{
    public string Path { get; } = path;
    public string Root { get; } = root;
}
