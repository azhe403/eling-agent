namespace Eling.Core.Projects;

/// <summary>
/// The one place a project root is turned into a comparable key.
/// </summary>
/// <remarks>
/// Three call sites used to normalize paths three different ways — trim only,
/// trim plus forward slashes, trim plus lower-case — and then compare with
/// <c>OrdinalIgnoreCase</c> on top. Any pair of those disagreed, so the same
/// folder could be recorded twice. There is one normal form now and callers
/// compare <c>root_key</c> directly.
/// <para>
/// Lower-casing is mildly wrong on a case-sensitive filesystem, where two
/// directories differing only in case are distinct. That failure merges them;
/// the alternative would split them, which is the direction that loses data.
/// </para>
/// </remarks>
public static class RootKeys
{
    public static string Of(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var full = Path.GetFullPath(root);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Trimming turns "C:\" into "C:", which .NET reads as current-drive
        // relative rather than the root. A filesystem root has nothing above it
        // to trim.
        if (trimmed.Length == 0) return full.ToLowerInvariant();

        var isDriveRoot = trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':';
        var isFileSystemRoot = trimmed.Length == 1 && full.Length > 1;

        return (isDriveRoot || isFileSystemRoot ? trimmed : full)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
    }

    public static bool Same(string left, string right)
        => string.Equals(Of(left), Of(right), StringComparison.Ordinal);
}