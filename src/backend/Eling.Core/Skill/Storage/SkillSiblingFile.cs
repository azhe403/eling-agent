namespace Eling.Core.Skill.Storage;

/// <summary>
/// One opaque payload file that belongs to a skill folder (spec §3.2/§3.3:
/// everything in the folder other than SKILL.md is copied verbatim, never
/// parsed). <see cref="Content"/> is raw bytes; the MCP layer is responsible
/// for base64 decoding before handing it to storage.
/// </summary>
public sealed record SkillSiblingFile
{
    /// <summary>
    /// <paramref name="relativePath"/> is relative to the skill folder. It
    /// must not be absolute; every segment must be a normal name (no
    /// <c>..</c>, no <c>.</c>, no empty segments, no <c>:</c>); forward or
    /// back slashes are both accepted as separators. Full validation happens
    /// in storage before any IO.
    /// </summary>
    /// <param name="relativePath">Path relative to the skill folder.</param>
    /// <param name="content">Raw bytes, written exactly as given.</param>
    public SkillSiblingFile(string relativePath, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(content);
        RelativePath = relativePath;
        Content = content;
    }

    public string RelativePath { get; }

    public byte[] Content { get; }
}
