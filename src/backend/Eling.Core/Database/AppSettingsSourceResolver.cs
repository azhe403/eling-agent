namespace Eling.Core.Database;

/// <summary>
/// Resolves where the connection scanner reads appsettings from. One resolver,
/// every host: the MCP stdio host and the dashboard HTTP host once pointed at
/// different sources — one file against the whole directory — so a workspace
/// with an appsettings.Staging.json showed that file over REST and never over
/// MCP. The resolver is the single place that decision lives now.
/// </summary>
public static class AppSettingsSourceResolver
{
    /// <summary>Environment variable that overrides the source; a file or a directory.</summary>
    public const string DetectSourceVariable = "ELING_DATABASE_DETECT_SOURCE";

    /// <summary>Every appsettings file, the base one and the environment ones.</summary>
    public const string FilePattern = "appsettings*.json";

    /// <summary>
    /// The file or directory the scanner reads. An explicit override wins and may
    /// name either a file or a directory. Otherwise the workspace directory wins
    /// when it holds any appsettings file, and the host content root is the
    /// fallback.
    /// </summary>
    /// <param name="contentRootPath">The host's content root, used as the fallback.</param>
    /// <param name="workspaceDirectory">
    /// The directory the agent was launched in. Defaults to the process working
    /// directory, which is the workspace the MCP client spawns the backend in.
    /// </param>
    /// <param name="configuredSource">
    /// The override value. Defaults to the environment variable, and is a
    /// parameter so a test can exercise the override without touching process
    /// state other tests observe.
    /// </param>
    public static string Resolve(
        string contentRootPath,
        string? workspaceDirectory = null,
        string? configuredSource = null)
    {
        var configured = configuredSource ?? Environment.GetEnvironmentVariable(DetectSourceVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var workspace = workspaceDirectory ?? Environment.CurrentDirectory;
        return HasAppSettings(workspace) ? workspace : contentRootPath;
    }

    /// <summary>True when the directory holds at least one appsettings file.</summary>
    public static bool HasAppSettings(string directory)
        => Directory.Exists(directory)
           && Directory.EnumerateFiles(directory, FilePattern).Any();
}
