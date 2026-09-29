namespace Eling.Backend.Dtos;

/// <summary>
/// Identity shown in the dashboard chrome, taken from the global git config.
/// </summary>
/// <param name="Name">
/// Display name from <c>user.name</c>, or <c>anonymous</c> when unset.
/// </param>
/// <param name="Email">
/// Address from <c>user.email</c>, or an empty string when unset. Empty rather
/// than a placeholder so the UI can hide the line instead of showing a fake
/// address.
/// </param>
public record GitIdentityDto(
    string Name,
    string Email);
