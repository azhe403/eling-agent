namespace Eling.Core.Scope;

public static class ProjectId
{
    public static string FromScope(ProjectScope? scope, bool isUserHome)
    {
        if (isUserHome) return "UserScope";
        var root = scope?.Root;
        if (string.IsNullOrWhiteSpace(root)) return "unknown";
        return Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}
