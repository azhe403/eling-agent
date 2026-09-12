namespace Eling.Core.Exceptions;

/// <summary>
/// Thrown when a project-scope write is attempted while no scope-chain level is
/// initialized (no <c>.eling</c> anywhere above the working directory). The
/// backend maps this to an <c>init-required</c> response asking the user for
/// consent before <c>memory_init_project</c> creates the directory.
/// </summary>
public sealed class ProjectScopeNotInitializedException(string cwd) : InvalidOperationException(
    $"No project scope initialized at '{cwd}'. Ask the user for approval, then call memory_init_project.")
{
    public string Cwd { get; } = cwd;
}
