using System.Text.RegularExpressions;

namespace Eling.Core.Matching;

/// <summary>
/// Shared glob matcher supporting <c>*</c>, <c>**</c>, and <c>?</c>. Matching is
/// case-insensitive and normalizes path separators to <c>/</c>. Used by intention
/// file-pattern triggers and by the project-scope policy.
/// </summary>
public static class GlobPattern
{
    public static bool IsMatch(string glob, string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(glob);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var regex = GlobToRegex(glob);
        var normalized = filePath.Replace('\\', '/');
        return regex.IsMatch(normalized);
    }

    private static Regex GlobToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob.Trim().Replace('\\', '/')).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", ".") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
