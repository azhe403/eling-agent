namespace Eling.Backend.Dtos;

/// <summary>
/// Request payload to update semantic judge settings.
/// </summary>
public sealed record UpdateJudgeConfigRequest(
    bool? Enabled = null,
    string? BaseUrl = null,
    string? Model = null,
    string? ApiKey = null,
    int? TimeoutSeconds = null);
