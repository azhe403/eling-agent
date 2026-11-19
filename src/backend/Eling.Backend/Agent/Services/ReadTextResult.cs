namespace Eling.Backend.Agent.Services;

/// <summary>Result of reading a text file (truncated at the read cap).</summary>
public sealed record ReadTextResult(
    string Content,
    bool Truncated);
