using Eling.Backend.Dtos;

namespace Eling.Backend.Identity;

/// <summary>
/// Resolves the identity the dashboard shows in its chrome, taken from the
/// global git config so the UI follows whoever is running it.
/// </summary>
public sealed class GitIdentityService(IGitConfigReader reader)
{
    /// <summary>
    /// Shown when git has no <c>user.name</c> — the key is unset, or git is
    /// not readable at all. Both cases are indistinguishable to this service
    /// by design, and neither is worth surfacing as an error.
    /// </summary>
    public const string FallbackName = "anonymous";

    public async Task<GitIdentityDto> GetIdentityAsync(CancellationToken cancellationToken)
    {
        // Two reads of the same global config; each is a few milliseconds and
        // the resolved executable is memoized, so batching would add coupling
        // for no measurable gain.
        var name = await reader.ReadGlobalAsync("user.name", cancellationToken);
        var email = await reader.ReadGlobalAsync("user.email", cancellationToken);

        return new GitIdentityDto(Resolve(name, FallbackName), Resolve(email, string.Empty));
    }

    private static string Resolve(string? configured, string fallback)
        => string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
}
