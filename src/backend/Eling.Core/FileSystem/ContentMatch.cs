namespace Eling.Core.FileSystem;

public sealed record ContentMatch(
    string Path,
    int LineNumber,
    string LineText);
