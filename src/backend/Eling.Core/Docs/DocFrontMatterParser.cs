namespace Eling.Core.Docs;

using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Parses optional YAML front-matter from managed documents.
/// Malformed front-matter is non-fatal and recorded as a finding.
/// </summary>
public static class DocFrontMatterParser
{
    private static readonly Regex FrontMatterRegex = new(
        @"^---\s*\n(?<content>.*?)\n---\s*\n",
        RegexOptions.Singleline | RegexOptions.Compiled);
    
    // Allow truly empty front-matter (--- followed immediately by ---)
    private static readonly Regex EmptyFrontMatterRegex = new(
        @"^---\s*\n---\s*\n",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Attempts to parse front-matter from the document content.
    /// Returns (frontMatter, remainingContent, errorMessage).
    /// If errorMessage is non-null, the front-matter was malformed and the
    /// document should be indexed with a finding rather than failing.
    /// </summary>
    public static (DocFrontMatter? FrontMatter, string RemainingContent, string? ErrorMessage) Parse(string content)
    {
        var match = FrontMatterRegex.Match(content);
        if (!match.Success)
        {
            // Try empty front-matter pattern
            var emptyMatch = EmptyFrontMatterRegex.Match(content);
            if (emptyMatch.Success)
            {
                return (new DocFrontMatter(), content[emptyMatch.Length..], null);
            }
            return (null, content, null);
        }

        var yaml = match.Groups["content"].Value;
        var remaining = content[match.Length..];

        try
        {
            var fm = Deserializer.Deserialize<DocFrontMatter>(yaml);
            // Empty front-matter (--- ---) returns null from deserializer; treat as empty object
            return (fm ?? new DocFrontMatter(), remaining, null);
        }
        catch (Exception ex)
        {
            // Malformed front-matter: return error but don't fail the whole document
            return (null, content, $"Front-matter parse error: {ex.Message}");
        }
    }
}

/// <summary>Front-matter structure for managed documents.</summary>
public sealed class DocFrontMatter
{
    public DocKind? Kind { get; set; }
    public string? Topic { get; set; }
    public int? Order { get; set; }
    public int? Status { get; set; }
    public string? SupersededBy { get; set; }
    public string? Spec { get; set; }
    public Dictionary<string, SectionFrontMatter>? Sections { get; set; }
}

/// <summary>Per-section front-matter for status and annotation.</summary>
public sealed class SectionFrontMatter
{
    public int? Status { get; set; }
    public string? Annotation { get; set; }
}