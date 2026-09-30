namespace Eling.Desktop.Models;

/// <summary>
/// One drive or directory in the host-browse tree. Plain data: the view binds to
/// it reflectively, so it carries no dependency on the view layer.
/// </summary>
public class HostEntry(string displayName, string fullPath, bool isDirectory)
{
    public string DisplayName { get; } = displayName;
    public string FullPath { get; } = fullPath;
    public bool IsDirectory { get; } = isDirectory;
}
