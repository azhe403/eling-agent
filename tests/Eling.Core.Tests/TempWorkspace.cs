namespace Eling.Core.Tests;

/// <summary>
/// A throwaway directory tree under the system temp folder. Every path a
/// test touches is created here, so no test depends on where the repository
/// happens to be checked out.
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    private readonly string _root;

    public TempWorkspace()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "eling-git-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Creates a directory under the workspace root and returns its full path.
    /// </summary>
    public string Folder(params string[] segments)
    {
        var path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
