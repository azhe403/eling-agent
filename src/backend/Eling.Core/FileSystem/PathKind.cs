namespace Eling.Core.FileSystem;

/// <summary>
/// Kind of a filesystem entry as seen from the sandbox. Symlinks are
/// reported as-is and never followed across the sandbox boundary.
/// </summary>
public enum PathKind
{
    NotFound,
    File,
    Directory,
    Symlink,
    Other
}
