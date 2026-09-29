namespace Eling.Backend.Identity;

/// <summary>
/// Reads a single value from the global git config.
/// </summary>
/// <remarks>
/// Returns <c>null</c> when the key is unset and when git cannot be read at
/// all (not installed, no global config, non-zero exit, timeout). Callers apply
/// their own fallback and do not need to tell those cases apart.
/// </remarks>
public interface IGitConfigReader
{
    Task<string?> ReadGlobalAsync(string key, CancellationToken cancellationToken);
}
