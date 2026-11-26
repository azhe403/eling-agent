namespace Eling.Core.Docs;

using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Parses optional YAML front-matter from Markdown documents.
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
    /// Returns a ParseResult containing frontMatter, remainingContent, and errorMessage.
    /// If errorMessage is non-null, the front-matter was malformed and the
    /// document should be indexed with a finding rather than failing.
    /// </summary>
    public static ParseResult Parse(string content, ILogger? logger = null)
    {
        var match = FrontMatterRegex.Match(content);
        if (!match.Success)
        {
            // Try empty front-matter pattern
            var emptyMatch = EmptyFrontMatterRegex.Match(content);
            if (emptyMatch.Success)
            {
                return new ParseResult(new DocFrontMatter(), content[emptyMatch.Length..], null);
            }
            return new ParseResult(null, content, null);
        }

        var yaml = match.Groups["content"].Value;
        var remaining = content[match.Length..];

        try
        {
            var fm = Deserializer.Deserialize<DocFrontMatter>(yaml);
            // Empty front-matter (--- ---) returns null from deserializer; treat as empty object
            return new ParseResult(fm ?? new DocFrontMatter(), remaining, null);
        }
        catch (Exception ex)
        {
            // Malformed front-matter: return error but don't fail the whole document
            logger?.LogWarning(ex, "Front-matter parse error");
            return new ParseResult(null, content, $"Front-matter parse error: {ex.Message}");
        }
    }
}