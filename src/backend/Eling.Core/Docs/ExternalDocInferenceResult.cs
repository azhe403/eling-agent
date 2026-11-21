namespace Eling.Core.Docs;

/// <summary>
/// Result of external document inference.
/// </summary>
public sealed record ExternalDocInferenceResult(
    DocKind Kind,
    string Topic,
    DocStatusValue Status,
    int Order);