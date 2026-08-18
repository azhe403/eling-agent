namespace Eling.Core;

/// <summary>
/// Locates the Eling repository root so runtime data lands in a stable place
/// regardless of the process working directory.
/// </summary>
public static class RepositoryRoot
{
    public const string MarkerFile = "Eling.slnx";

    /// <summary>
    /// Returns the absolute path of the first ancestor (of the current directory
    /// or the application base directory) that contains <see cref="MarkerFile"/>.
    /// Falls back to the current directory when no repository marker is found,
    /// e.g. for a deployed/published executable.
    /// </summary>
    public static string Find()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, MarkerFile)))
                    return dir.FullName;

                dir = dir.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }
}
