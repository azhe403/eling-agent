namespace Eling.Core.Memory.Storage;

/// <summary>One FTS hit: the memory id and its BM25 rank (negative; more-negative is better).</summary>
internal readonly record struct FtsRank(
    string Id,
    double Score);
