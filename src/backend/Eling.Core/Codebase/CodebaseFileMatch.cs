namespace Eling.Core.Codebase;

/// <summary>
/// Which part of an indexed file's path a file-search needle is matched
/// against. The dashboard's file scopes offer both: the whole
/// workspace-relative path, so a folder name narrows the list, or the file
/// name alone, so a name the user remembers finds exactly that file.
/// </summary>
public enum CodebaseFileMatch
{
    /// <summary>The whole workspace-relative path, folders included.</summary>
    Path,

    /// <summary>The file name only — the last segment of the path.</summary>
    FileName,
}
