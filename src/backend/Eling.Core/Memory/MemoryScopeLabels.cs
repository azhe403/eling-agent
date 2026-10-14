namespace Eling.Core.Memory;

/// <summary>
/// Single home for the wire spelling of <see cref="MemoryScopeKind"/>
/// ("project", "project-local", "global"). Pure.
/// </summary>
public static class MemoryScopeLabels
{
    /// <summary>Wire spelling of the default save tier — the value tool parameter defaults use.</summary>
    public const string DefaultSaveScope = "project-local";

    public static string ToWireString(MemoryScopeKind scope) => scope switch
    {
        MemoryScopeKind.Global => "global",
        MemoryScopeKind.ProjectLocal => "project-local",
        _ => "project",
    };
}
