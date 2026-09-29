namespace Eling.Backend.Tests;

/// <summary>
/// Groups the tests that override <c>ELING_DATA_DIR</c>. xUnit runs different
/// collections in parallel, and that variable is process-global, so
/// <c>ElingPaths.ResolveCodebaseDbPath</c> reads whichever test last set it.
/// Sharing one collection makes these classes run sequentially instead of
/// racing each other over the same path resolution.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ElingDataDirCollection
{
    public const string Name = "ElingDataDirEnv";
}
