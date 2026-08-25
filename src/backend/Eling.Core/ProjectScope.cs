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

    /// <param name="stopAtDirectory">
    /// Optional ceiling for the upward walk (test seam): the walk inspects this
    /// directory last and never goes above it. Production callers omit it.
    /// </param>
    public static ProjectScope Discover(string? startDirectory = null, string? stopAtDirectory = null)
    {
        var start = string.IsNullOrWhiteSpace(startDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(startDirectory);
        var stopAt = string.IsNullOrWhiteSpace(stopAtDirectory)
            ? null
            : Path.GetFullPath(stopAtDirectory);

        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, DataDirectoryName);
            if (Directory.Exists(candidate))
            {
                return new ProjectScope(current.FullName);
            }

            if (stopAt is not null &&
                string.Equals(
                    current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    stopAt.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = current.Parent;
        }

        return new ProjectScope(start);
    }
}
