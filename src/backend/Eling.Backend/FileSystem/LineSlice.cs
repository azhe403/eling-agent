namespace Eling.Backend.FileSystem;

/// <summary>A slice of lines and the bounds around it.</summary>
internal sealed record LineSlice(
    string Slice,
    int EndLine,
    int TotalLines);
