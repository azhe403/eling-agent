namespace Eling.Core.Memory;

/// <summary>
/// Single home for the wire spelling of <see cref="MemoryScopeKind"/>
/// ("project", "project-local", "global"). Pure.
/// </summary>
public static class MemoryScopeLabels
{
    public static string ToWireString(MemoryScopeKind scope) => scope switch
    {
        MemoryScopeKind.Global => "global",
        MemoryScopeKind.ProjectLocal => "project-local",
        _ => "project",
    };
}
