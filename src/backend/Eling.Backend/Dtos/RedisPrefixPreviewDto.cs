namespace Eling.Backend.Dtos;

/// <summary>
/// The flat list of keys under one prefix, for the subtree delete dialog: the
/// capped list the operator confirms against, the full count, and whether the
/// scan budget cut it short.
/// </summary>
public sealed record RedisPrefixPreviewDto(
    string Prefix,
    string[] Keys,
    long Total,
    bool Truncated);
