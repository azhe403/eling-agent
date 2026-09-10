using System.ComponentModel;
using Eling.Backend.Dtos;
using Eling.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Creates a project `.eling` directory after user consent. This is the ONLY
/// code path that creates a project scope — the backend never auto-creates
/// one. Also appends the standard runtime .gitignore patterns at the nearest
/// git repo root when they are missing (conservative: no-op when present;
/// failure is surfaced as a warning, never thrown).
/// </summary>
[McpServerToolType]
public sealed class MemoryInitProjectTool
{
    private static readonly string[] RuntimeGitignorePatterns =
    [
        ".eling/index.db*",
        ".eling/*.db-journal",
        ".eling/*.db-wal",
        ".eling/runtime/"
    ];

    private readonly string _cwd;
    private readonly string? _userHomeDirectory;
    private readonly ILogger<MemoryInitProjectTool>? _logger;

    public MemoryInitProjectTool(string? cwd = null, string? userHomeDirectory = null, ILogger<MemoryInitProjectTool>? logger = null)
    {
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : Path.GetFullPath(cwd);
        _userHomeDirectory = string.IsNullOrWhiteSpace(userHomeDirectory)
            ? null
            : Path.GetFullPath(userHomeDirectory);
        _logger = logger;
    }

    [McpServerTool(Name = "memory_init_project"), Description("Create a project .eling directory at the current working directory after user consent. Never auto-created by the backend: run this only when the user explicitly approves adopting the current folder as a project scope.")]
    public Task<ProjectInitResultDto> InitAsync()
    {
        var userHome = _userHomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var isUserHome = !string.IsNullOrWhiteSpace(userHome) &&
            string.Equals(
                _cwd.TrimEnd(Path.DirectorySeparatorChar),
                userHome.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

        if (isUserHome)
        {
            return Task.FromResult(new ProjectInitResultDto("rejected-user-home", null, []));
        }

        var chain = ScopeChain.Discover(_cwd);
        if (chain.HasOwnScope)
        {
            return Task.FromResult(new ProjectInitResultDto("already-initialized", chain.Head?.Root, ChainRoots(chain)));
        }

        var memoriesDir = Path.Combine(_cwd, ProjectScope.DataDirectoryName, "memories");
        Directory.CreateDirectory(memoriesDir);

        var warning = UpdateGitignoreAtNearestRepo(_cwd);

        var fresh = ScopeChain.Discover(_cwd);
        return Task.FromResult(new ProjectInitResultDto(
            "created",
            fresh.Head?.Root,
            ChainRoots(fresh),
            warning));
    }

    private static IReadOnlyCollection<string> ChainRoots(ScopeChain chain)
        => chain.Levels.Select(l => l.Root).ToList().AsReadOnly();

    private static string? UpdateGitignoreAtNearestRepo(string cwd)
    {
        try
        {
            var repoRoot = FindNearestGitRoot(cwd);
            if (repoRoot is null)
            {
                return null;
            }

            var gitignorePath = Path.Combine(repoRoot, ".gitignore");
            var existing = File.Exists(gitignorePath) ? File.ReadAllText(gitignorePath) : "";
            var missing = RuntimeGitignorePatterns.Where(p => !existing.Contains(p, StringComparison.Ordinal)).ToList();
            if (missing.Count == 0)
            {
                return null;
            }

            var updated = existing.EndsWith('\n') || existing.Length == 0
                ? existing + string.Join('\n', missing) + "\n"
                : existing + "\n" + string.Join('\n', missing) + "\n";
            File.WriteAllText(gitignorePath, updated);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not update .gitignore: {ex.Message}";
        }
    }

    private static string? FindNearestGitRoot(string start)
    {
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }
}