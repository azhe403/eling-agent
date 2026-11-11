namespace Eling.Backend.Shell;

/// <summary>
/// The directory the backend process was launched in: the workspace an agent
/// command operates on. The filesystem sandbox may be rooted at a parent
/// <c>.eling</c> scope, but a shell is about the code being worked on, so
/// this is injected separately from <see cref="Eling.Core.FileSystem.IFileSystemService"/>.
/// </summary>
public sealed class ShellSessionRoot
{
    public ShellSessionRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentNullException(nameof(root));
        }

        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
}
