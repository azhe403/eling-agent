namespace Eling.Core.Workspaces;

/// <summary>
/// The one place a wire value becomes a workspace group mode. The coordinator's
/// <c>group</c> parameter, the desktop client and the dashboard client all speak
/// the same three words, so the vocabulary lives here instead of being spelled
/// out as a switch in every layer.
/// </summary>
public static class WorkspaceGroupModes
{
    /// <summary>
    /// Parses a wire value into a mode.
    /// </summary>
    /// <param name="value">The wire value, case-insensitive.</param>
    /// <param name="mode">The parsed mode, or <c>default</c> when unknown.</param>
    /// <returns>True when <paramref name="value"/> names a known mode.</returns>
    public static bool TryParse(string? value, out WorkspaceGroupMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "flat":
                mode = WorkspaceGroupMode.Flat;
                return true;
            case "repo":
                mode = WorkspaceGroupMode.Repo;
                return true;
            case "folder":
                mode = WorkspaceGroupMode.Folder;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    /// <summary>The wire value of a mode.</summary>
    public static string ToWireValue(this WorkspaceGroupMode mode) => mode switch
    {
        WorkspaceGroupMode.Flat => "flat",
        WorkspaceGroupMode.Repo => "repo",
        WorkspaceGroupMode.Folder => "folder",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown workspace group mode."),
    };
}
