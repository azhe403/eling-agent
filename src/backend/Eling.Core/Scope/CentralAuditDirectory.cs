namespace Eling.Core.Scope;

public static class CentralAuditDirectory
{
    public const string DirectoryName = "audit";

    /// <summary>
    /// Resolves the central, machine-global audit store beside the log directory.
    /// It is global on purpose: entries carry their originating <c>scope</c> and
    /// <c>projectRoot</c> as fields, so one store serves every project. Resolution
    /// goes through <see cref="ElingPaths"/> so the <c>ELING_DATA_DIR</c> override
    /// isolates test scopes exactly as it already does for logs.
    /// </summary>
    public static string Resolve(string? xdgDataHome = null, string? userHome = null, string? elingDataDir = null)
    {
        var path = Path.Combine(ElingPaths.ResolveDataDir(xdgDataHome, userHome, elingDataDir), DirectoryName);
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
        }
        return path;
    }
}
