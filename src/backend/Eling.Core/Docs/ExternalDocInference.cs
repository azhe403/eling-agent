namespace Eling.Core.Docs;

using System.Text.RegularExpressions;

/// <summary>
/// Infers metadata for external documents (those in <c>docs/</c>) from their
/// location, first heading, and filename. Never writes front-matter.
/// </summary>
public static class ExternalDocInference
{
    private static readonly Regex HeadingRegex = new(
        @"^#\s+(.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Infers kind, topic, status, and read_order for an external document.
    /// </summary>
    public static (DocKind Kind, string Topic, DocStatusValue Status, int Order) Infer(
        string relativePath,
        string content,
        string? explicitKind = null)
    {
        // Kind from folder or explicit
        var kind = explicitKind switch
        {
            "spec" => DocKind.Spec,
            "plan" => DocKind.Plan,
            "doc" => DocKind.Doc,
            "scratch" => DocKind.Scratch,
            _ => InferKindFromPath(relativePath),
        };

        // Topic from parent folder name
        var topic = InferTopicFromPath(relativePath);

        // Status default per kind
        var status = DocStatusValue.DefaultFor(kind);

        // Order from filename (numeric prefix) or 0
        var order = InferOrderFromFilename(relativePath);

        return (kind, topic, status, order);
    }

    private static DocKind InferKindFromPath(string relativePath)
    {
        var lower = relativePath.ToLowerInvariant();
        if (lower.Contains("/specs/") || lower.Contains("\\specs\\"))
            return DocKind.Spec;
        if (lower.Contains("/plans/") || lower.Contains("\\plans\\"))
            return DocKind.Plan;
        if (lower.Contains("/scratch/") || lower.Contains("\\scratch\\"))
            return DocKind.Scratch;
        return DocKind.Doc;
    }

    private static string InferTopicFromPath(string relativePath)
    {
        // Get the folder directly under docs/ or .eling/docs/
        var normalized = relativePath.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        
        // Find "docs" or ".eling/docs" and take the next segment
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("docs", StringComparison.OrdinalIgnoreCase) ||
                (parts[i].Equals(".eling", StringComparison.OrdinalIgnoreCase) && 
                 i + 1 < parts.Length && parts[i + 1].Equals("docs", StringComparison.OrdinalIgnoreCase)))
            {
                if (i + 1 < parts.Length)
                    return parts[i + 1];
            }
        }
        
        // Fallback: parent folder of the file
        var parent = Path.GetDirectoryName(relativePath);
        if (!string.IsNullOrEmpty(parent))
        {
            var parentParts = parent.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            return parentParts.LastOrDefault() ?? "general";
        }
        return "general";
    }

    private static int InferOrderFromFilename(string relativePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        var match = Regex.Match(fileName, @"^(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var num))
            return num;
        return 0;
    }
}