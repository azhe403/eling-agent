namespace Eling.Core.Projects;

/// <summary>
/// The identity a running project is logged and reported under: the name of
/// the folder the host opened.
/// </summary>
/// <remarks>
/// The identity is the workspace, not the <c>.eling</c> scope that owns the
/// memories. Those differ whenever the opened folder sits inside a larger
/// scope: opening <c>&lt;repo&gt;/service</c> under a scope at <c>&lt;repo&gt;</c>
/// writes memories to the parent scope, but the project is still the opened
/// service, and two services under one scope are two different projects. The
/// scope root stays visible in the runtime registration as
/// <c>HeadScopeRoot</c>, where it belongs.
/// </remarks>
public static class ProjectId
{
    /// <summary>
    /// The project identity for an opened workspace.
    /// </summary>
    /// <param name="workspaceRoot">
    /// The folder the host opened. Null or blank yields <c>unknown</c>.
    /// </param>
    /// <param name="isUserHome">
    /// True for a session at the user home: it has no project, so the sentinel
    /// <c>UserScope</c> is used.
    /// </param>
    public static string FromWorkspace(string? workspaceRoot, bool isUserHome)
    {
        if (isUserHome) return "UserScope";
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return "unknown";
        var name = Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }
}
