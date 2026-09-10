namespace Eling.Core;

/// <summary>
/// Thrown when a project-scope write is attempted while no scope-chain level is
/// initialized (no <c>.eling</c> anywhere above the working directory). The
/// backend maps this to an <c>init-required</c> response asking the user for
/// consent before <c>memory_init_project</c> creates the directory.
/// </summary>
public sealed class ProjectScopeNotInitializedException(string cwd) : InvalidOperationException(
    $"No project scope initialized at '{cwd}'. Ask the user for approval, then call memory_init_project.")
{
    public string Cwd { get; } = cwd;
}

public readonly record struct SaveResult(Memory Memory, SaveAction Action, Memory? Previous = null)
{
    public MemoryId Id => Memory.Id;
    public MemoryType Type => Memory.Type;
    public MemoryStatus Status => Memory.Status;
    public string Content => Memory.Content;
    public IReadOnlyCollection<string> Tags => Memory.Tags;
    public DateTimeOffset CreatedAt => Memory.CreatedAt;
    public DateTimeOffset UpdatedAt => Memory.UpdatedAt;
    public string? Source => Memory.Source;

    public static implicit operator Memory(SaveResult result) => result.Memory;
}

public readonly record struct ScopedSaveResult(ScopedMemory Scoped, SaveAction Action, ScopedMemory? Previous = null)
{
    public Memory Memory => Scoped.Memory;
    public MemoryScopeKind Scope => Scoped.Scope;
    public string? ProjectRoot => Scoped.ProjectRoot;
    public MemoryId Id => Scoped.Id;

    public static implicit operator ScopedMemory(ScopedSaveResult result) => result.Scoped;
    public static implicit operator Memory(ScopedSaveResult result) => result.Memory;
}
