namespace Eling.Core;

public sealed class UserScope
{
    public string Root { get; }
    public string ConfigDirectory { get; }
    public string RuntimeDirectory { get; }

    /// <summary>
    /// Global memory storage root — physically separated from any project .eling.
    /// Points to <user-data>/eling/ itself; FileSystemMemoryStorage appends /memories.
    /// </summary>
    public string GlobalDataDirectory => Root;

    public UserScope(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        ConfigDirectory = Path.Combine(Root, "config");
        RuntimeDirectory = Path.Combine(Root, "runtime");
    }

    public static UserScope Resolve(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return new UserScope(overridePath);
        }

        // Single global location on every platform: ~/.config/eling
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new UserScope(Path.Combine(home, ".config", "eling"));
    }
}
