namespace Eling.Core.Docs;

/// <summary>Information about a heading found during chunking.</summary>
public sealed record HeadingInfo(
    int Level,
    string Text,
    int LineIndex);