namespace Eling.Backend.Logging;

/// <summary>
/// The on-disk shape of <c>log.json</c>. The format stays a raw string so the
/// file remains hand-editable; <see cref="LogConfigStore"/> resolves it through
/// <c>LogFormatExtensions.Resolve</c>.
/// </summary>
public sealed record LogConfigDto(string? Format);
