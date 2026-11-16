using Eling.Backend.Dtos;
using Eling.Core.Scope;
using Eling.Core.Skill;
using Eling.Core.Skill.Storage;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Shared plumbing for the skill_* MCP tools: SKILL.md path resolution,
/// base64 sibling-file decoding with traversal-safe validation, cross-
/// reference validation, and the domain-exception-to-tool-error mapping.
/// Kept internal; the tools themselves are the public surface.
/// </summary>
internal static class SkillTools
{
    /// <summary>Absolute path of the SKILL.md for <paramref name="name"/> under the catalog root.</summary>
    public static string MarkdownPath(string? catalogRoot, string name)
        => Path.GetFullPath(Path.Combine(
            catalogRoot ?? SkillCatalogPaths.CatalogDirectory,
            SkillCatalogPaths.SkillsSubdir,
            name,
            "SKILL.md"));

    /// <summary>Absolute path of the folder holding <paramref name="name"/> under the catalog root.</summary>
    public static string SkillFolderPath(string? catalogRoot, string name)
        => Path.GetFullPath(Path.Combine(
            catalogRoot ?? SkillCatalogPaths.CatalogDirectory,
            SkillCatalogPaths.SkillsSubdir,
            name));

    /// <summary>
    /// Decodes and validates every sibling file entry, rejecting paths that
    /// escape the skill folder before any filesystem write happens. Throws
    /// <see cref="SkillValidationException"/> on the first bad entry.
    /// </summary>
    public static IReadOnlyList<SkillSiblingFile> PrepareSiblingFiles(
        SkillSiblingFileInput[]? inputs,
        string skillFolder)
    {
        if (inputs is null || inputs.Length == 0)
        {
            return [];
        }

        var files = new List<SkillSiblingFile>(inputs.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            if (input is null)
            {
                throw new SkillValidationException("siblingFiles entries must not be null.");
            }

            var relativePath = input.RelativePath?.Trim();
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new SkillValidationException("Sibling file 'relativePath' must not be empty.");
            }

            if (relativePath.Contains('\\'))
            {
                throw new SkillValidationException(
                    $"Sibling file path '{relativePath}' must use forward slashes ('/'); backslash paths are rejected.");
            }

            if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('/'))
            {
                throw new SkillValidationException(
                    $"Sibling file path '{relativePath}' is absolute; it must be relative to the skill folder.");
            }

            var segments = relativePath.Split('/');
            foreach (var segment in segments)
            {
                if (segment.Length == 0 || segment is "." or ".." || segment.Contains(':'))
                {
                    throw new SkillValidationException(
                        $"Sibling file path '{relativePath}' contains an illegal segment ('{segment}'); "
                        + "empty, '.', '..' and drive-qualified segments are not allowed.");
                }

                if (segment == "~")
                {
                    throw new SkillValidationException(
                        $"Sibling file path '{relativePath}' uses '~'; tilde expansion is not supported.");
                }
            }

            if (string.Equals(segments[^1], "SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                throw new SkillValidationException(
                    "Sibling file path 'SKILL.md' is reserved for the skill document; choose a different relativePath.");
            }

            var combined = Path.GetFullPath(Path.Combine(skillFolder, Path.Combine(segments)));
            var fullBase = Path.GetFullPath(skillFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!combined.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
            {
                throw new SkillValidationException(
                    $"Sibling file path '{relativePath}' escapes the skill folder; refusing.");
            }

            if (!seen.Add(combined))
            {
                throw new SkillValidationException($"Duplicate sibling file path '{relativePath}'.");
            }

            byte[] content;
            try
            {
                content = Convert.FromBase64String(input.ContentBase64 ?? string.Empty);
            }
            catch (FormatException ex)
            {
                throw new SkillValidationException(
                    $"Sibling file '{relativePath}': 'contentBase64' is not valid Base64.", ex);
            }

            files.Add(new SkillSiblingFile(relativePath, content));
        }

        return files;
    }

    /// <summary>
    /// Validates the cross-reference entries of skill_save against the
    /// controlled vocabularies. Snippet references must not carry a scope;
    /// memory references require one from the vocabulary.
    /// </summary>
    public static IReadOnlyList<RelatedReference> ValidateRelatedReferences(
        SkillRelatedReferenceInput[]? inputs,
        bool snippets)
    {
        var label = snippets ? "relatedSnippets" : "relatedMemories";
        if (inputs is null || inputs.Length == 0)
        {
            return [];
        }

        var list = new List<RelatedReference>(inputs.Length);
        for (var i = 0; i < inputs.Length; i++)
        {
            var input = inputs[i];
            if (input is null)
            {
                throw new SkillValidationException($"{label}[{i}] must not be null.");
            }

            var id = input.Id?.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new SkillValidationException($"{label}[{i}].id must not be empty.");
            }

            var relation = input.Relation?.Trim().ToLowerInvariant();
            if (!SkillVocabulary.IsRelation(relation))
            {
                throw new SkillValidationException(
                    $"{label}[{i}].relation '{input.Relation}' must be one of: {string.Join(", ", SkillVocabulary.Relations)}.");
            }

            var scope = input.Scope?.Trim().ToLowerInvariant();
            if (snippets)
            {
                if (scope is not null)
                {
                    throw new SkillValidationException(
                        $"{label}[{i}].scope must be omitted: snippet references do not carry a memory scope.");
                }

                list.Add(new RelatedReference(id, null, relation!));
            }
            else
            {
                if (!SkillVocabulary.IsMemoryScope(scope))
                {
                    throw new SkillValidationException(
                        $"{label}[{i}].scope '{input.Scope}' must be one of: {string.Join(", ", SkillVocabulary.MemoryScopes)}.");
                }

                list.Add(new RelatedReference(id, scope, relation!));
            }
        }

        return list;
    }

    /// <summary>
    /// Rewrites the domain exceptions into tool-facing ones whose messages
    /// carry the markers the agent (and the ToolExceptionFilter IsError text)
    /// can act on: "externally managed", "not found", "name conflict", "invalid".
    /// Anything not on the list propagates unchanged.
    /// </summary>
    public static Exception MapException(Exception ex) => ex switch
    {
        SkillReadOnlyException e => new SkillReadOnlyException(
            $"External skills are externally managed; Eling never writes or renames them: {e.Message}", e),
        SkillNotFoundException e => new SkillNotFoundException($"Skill not found: {e.Message}", e),
        SkillConflictException e => new SkillConflictException($"Skill name conflict: {e.Message}", e),
        SkillValidationException e => new SkillValidationException($"Invalid skill input: {e.Message}", e),
        ArgumentException e => new ArgumentException($"Invalid skill input: {e.Message}", e.ParamName, e),
        KeyNotFoundException e => new SkillNotFoundException($"Skill not found: {e.Message}", e),
        _ => ex,
    };
}
