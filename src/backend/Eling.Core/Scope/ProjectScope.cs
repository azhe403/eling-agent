namespace Eling.Core.Scope;

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
    /// <param name="userHomeDirectory">
    /// Optional user-home override (test seam): directories at or above this
    /// path are never treated as project levels. Production callers omit it and
    /// the real user profile is used.
    /// </param>
    public static ProjectScope Discover(string? startDirectory = null, string? stopAtDirectory = null, string? userHomeDirectory = null)
    {
        var start = string.IsNullOrWhiteSpace(startDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(startDirectory);
        return DiscoverChain(start, stopAtDirectory, userHomeDirectory).FirstOrDefault() ?? new ProjectScope(start);
    }

    /// <param name="stopAtDirectory">
    /// Optional ceiling for the upward walk (test seam): the walk inspects this
    /// directory last and never goes above it. Production callers omit it.
    /// </param>
    /// <param name="userHomeDirectory">
    /// Optional user-home override (test seam): an `.eling` directly inside the
    /// user home is never a project level. Production callers omit it and the
    /// real user profile is used.
    /// </param>
    public static IReadOnlyList<ProjectScope> DiscoverChain(string? startDirectory = null, string? stopAtDirectory = null, string? userHomeDirectory = null)
    {
        var start = string.IsNullOrWhiteSpace(startDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(startDirectory);
        var stopAt = string.IsNullOrWhiteSpace(stopAtDirectory)
            ? null
            : Path.GetFullPath(stopAtDirectory);
        var userHome = string.IsNullOrWhiteSpace(userHomeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(userHomeDirectory);

        var levels = new List<ProjectScope>();
        var current = new DirectoryInfo(start);

        while (current is not null)
        {
            // .eling is only a project directory when NOT directly in user home
            // root (user-level data must strictly live under ~/.config/eling/).
            var isUserHome = !string.IsNullOrWhiteSpace(userHome) &&
                string.Equals(
                    current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    userHome.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);

            var candidate = Path.Combine(current.FullName, DataDirectoryName);
            if (!isUserHome && Directory.Exists(candidate))
            {
                levels.Add(new ProjectScope(current.FullName));
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

        return levels.AsReadOnly();
    }
}
