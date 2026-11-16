namespace Eling.Core.Scope;

/// <summary>
/// Single home for every path constant the skill subsystem uses: the
/// machine-global catalog rooted at <c>~/.agents</c>, its trash, and the
/// dedicated index database under the Eling runtime directory. Nothing
/// outside this class may spell these paths. Pure: computes paths only,
/// never touches disk.
/// </summary>
public static class SkillCatalogPaths
{
    public const string AgentsDirName = ".agents";
    public const string SkillsSubdir = "skills";
    public const string TrashDirName = ".trash";
    // Not a const: Path.DirectorySeparatorChar is not a compile-time constant.
    public static readonly string TrashSubdir = TrashDirName + Path.DirectorySeparatorChar + SkillsSubdir;
    public const string IndexFileName = "skill-index.db";

    /// <summary>The catalog root: <c>~/.agents</c>. Skills live in <c>skills/</c> beneath it.</summary>
    public static string CatalogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AgentsDirName);

    /// <summary>The skills folder: <c>~/.agents/skills</c>.</summary>
    public static string SkillsDirectory => Path.Combine(CatalogDirectory, SkillsSubdir);

    /// <summary>The catalog-local trash: <c>~/.agents/.trash/skills</c>. Eling is the only writer here.</summary>
    public static string TrashDirectory => Path.Combine(CatalogDirectory, TrashSubdir);

    /// <summary>
    /// The dedicated skill index database: <c>&lt;dataRoot&gt;/runtime/skill-index.db</c>.
    /// Kept separate from the memories/snippets index so the skill subsystem owns
    /// its own schema and migration lifecycle. Honors the same
    /// <c>ELING_DATA_DIR</c>/<c>XDG_DATA_HOME</c> overrides as the rest of Eling.
    /// </summary>
    public static string IndexFilePath =>
        Path.Combine(ElingPaths.ResolveRuntimeDir(null, null, null), IndexFileName);

    /// <summary>
    /// The folder for <paramref name="name"/>: <c>~/.agents/skills/&lt;name&gt;</c>.
    /// The name must be a valid kebab-case skill name — this is also what keeps
    /// a crafted name from escaping the catalog root.
    /// </summary>
    public static string SkillFolder(string name)
    {
        if (!Eling.Core.Skill.Skill.ValidateName(name))
        {
            throw new ArgumentException(
                "Skill folder name must be lowercase kebab-case.", nameof(name));
        }

        return Path.Combine(SkillsDirectory, name);
    }
}
