namespace Eling.Core;

public sealed class ProjectScope
{
    public const string DataDirectoryName = ".eling";

    public string Root { get; }
    public string DataDirectory { get; }

    public ProjectScope(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        DataDirectory = Path.Combine(Root, DataDirectoryName);
    }

    public static ProjectScope Discover(string? startDirectory = null)
    {
        var start = string.IsNullOrWhiteSpace(startDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(startDirectory);

        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, DataDirectoryName);
            if (Directory.Exists(candidate))
            {
                return new ProjectScope(current.FullName);
            }

            current = current.Parent;
        }

        return new ProjectScope(start);
    }
}
