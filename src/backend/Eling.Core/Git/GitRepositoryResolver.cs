namespace Eling.Core.Git;

/// <summary>
/// Finds the git repository a directory belongs to, without running git.
/// </summary>
/// <remarks>
/// The dashboard groups workspaces by repository, and a repository is only
/// knowable from the working copy's own metadata. Two markers exist on disk:
/// the main checkout carries a <c>.git</c> DIRECTORY, and a linked worktree
/// carries a <c>.git</c> FILE pointing at
/// <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;</c>. That worktree directory holds
/// a <c>commondir</c> file naming the main repository's <c>.git</c> — the one
/// place every checkout of a repository agrees on its identity.
/// <para>
/// Reading those files beats shelling out to <c>git rev-parse</c>: the runtime
/// list is fetched on every dashboard page load and on every runtime event, and
/// one process per workspace would cost more than the answer is worth. It also
/// keeps the lookup working on a machine without the git executable.
/// </para>
/// </remarks>
public static class GitRepositoryResolver
{
    private const string GitDirName = ".git";
    private const string GitDirPointerPrefix = "gitdir:";
    private const string WorktreesFolderName = "worktrees";

    /// <summary>
    /// The main worktree root of the repository containing <paramref name="path"/>,
    /// or null when the path is not inside a git repository.
    /// </summary>
    /// <param name="path">
    /// A directory inside the working copy. A relative path is rejected rather
    /// than resolved against the current directory: a stray relative root would
    /// silently attribute a workspace to whatever repository the process happens
    /// to run in.
    /// </param>
    public static string? ResolveRepositoryRoot(string? path)
    {
        var start = Normalize(path);
        if (start is null) return null;

        var directory = start;
        while (true)
        {
            var marker = Path.Combine(directory, GitDirName);
            if (Directory.Exists(marker))
            {
                // A .git directory marks the main checkout, so the checkout root
                // is the repository root.
                return directory;
            }
            if (File.Exists(marker))
            {
                // A .git file marks a linked worktree. Its main repository is the
                // only thing worth reporting, and an unreadable pointer leaves the
                // worktree unplaceable — report nothing rather than guess, so an
                // unresolvable worktree never gets folded into some other repo.
                return ResolveWorktreeRepositoryRoot(directory, marker);
            }

            var parent = Directory.GetParent(directory);
            if (parent is null) return null;
            directory = parent.FullName;
        }
    }

    /// <summary>
    /// The main repository of a linked worktree, read from the worktree's own
    /// <c>.git</c> pointer file.
    /// </summary>
    private static string? ResolveWorktreeRepositoryRoot(string worktreeRoot, string pointerFile)
    {
        var gitDir = ReadGitDir(pointerFile, worktreeRoot);
        if (gitDir is null) return null;

        var commonDir = ResolveCommonDir(gitDir);
        if (commonDir is null) return null;

        // The common directory IS the main repository's .git folder.
        return Directory.GetParent(commonDir)?.FullName;
    }

    /// <summary>
    /// The git directory a worktree's <c>.git</c> file points at, or null when
    /// the file is missing, empty or not a pointer.
    /// </summary>
    private static string? ReadGitDir(string pointerFile, string worktreeRoot)
    {
        var line = ReadFirstLine(pointerFile);
        if (line is null) return null;

        var trimmed = line.Trim();
        if (!trimmed.StartsWith(GitDirPointerPrefix, StringComparison.OrdinalIgnoreCase)) return null;

        var value = trimmed[GitDirPointerPrefix.Length..].Trim();
        if (value.Length == 0) return null;

        try
        {
            // Git writes an absolute path here. A relative one is resolved
            // against the worktree, which is where git would have resolved it.
            return Path.GetFullPath(
                Path.IsPathRooted(value) ? value : Path.Combine(worktreeRoot, value));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The main repository's <c>.git</c> directory for a worktree's git
    /// directory, or null when it cannot be established.
    /// </summary>
    private static string? ResolveCommonDir(string gitDir)
    {
        // The canonical answer: the worktree's gitdir names the main
        // repository's .git directory, relative to itself.
        var commonDirFile = Path.Combine(gitDir, "commondir");
        var value = ReadFirstLine(commonDirFile);
        if (!string.IsNullOrEmpty(value))
        {
            try
            {
                return Path.GetFullPath(Path.Combine(gitDir, value));
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (PathTooLongException)
            {
                return null;
            }
        }

        // Fallback for a worktree whose commondir is missing: the gitdir sits at
        // <main>/.git/worktrees/<name>, so the main .git is two levels up.
        var worktrees = Directory.GetParent(gitDir);
        if (worktrees is null) return null;
        if (!string.Equals(worktrees.Name, WorktreesFolderName, StringComparison.OrdinalIgnoreCase))
            return null;

        return Directory.GetParent(worktrees.FullName)?.FullName;
    }

    /// <summary>
    /// The first line of a file, or null when the file is missing or unreadable.
    /// </summary>
    private static string? ReadFirstLine(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            return reader.ReadLine();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A rooted, separator-free directory path, or null when the input cannot
    /// be one.
    /// </summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Path.IsPathRooted(path)) return null;

        try
        {
            var full = Path.GetFullPath(path);
            if (full.Length > 1)
            {
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return full.Length == 0 ? null : full;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
    }
}
