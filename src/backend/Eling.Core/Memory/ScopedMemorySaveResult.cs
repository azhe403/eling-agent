namespace Eling.Core.Memory;

public readonly record struct ScopedMemorySaveResult(
    ScopedMemory Scoped,
    SaveAction Action,
    ScopedMemory? Previous = null,
    IReadOnlyCollection<MemoryNearMatch>? NearMatches = null,
    string? Reason = null,
    double? MatchScore = null)
{
    public Memory Memory => Scoped.Memory;
    public MemoryScopeKind Scope => Scoped.Scope;
    public string? ProjectRoot => Scoped.ProjectRoot;
    public string? ProjectName => Scoped.ProjectRoot is null ? null : Path.GetFileName(Scoped.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar));
    public MemoryId Id => Scoped.Id;
    public IReadOnlyCollection<MemoryNearMatch> NearMatches { get; init; } = NearMatches ?? [];

    public static implicit operator ScopedMemory(ScopedMemorySaveResult result) => result.Scoped;
    public static implicit operator Memory(ScopedMemorySaveResult result) => result.Memory;
}
