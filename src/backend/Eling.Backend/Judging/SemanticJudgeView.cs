namespace Eling.Backend.Judging;

/// <summary>
/// Settings view for the judge, safe to hand to a UI or an HTTP response: it reports
/// whether a key is present but never carries the key itself.
/// </summary>
/// <param name="Enabled">Whether the judge is switched on.</param>
/// <param name="BaseUrl">Configured provider endpoint, if any.</param>
/// <param name="Model">Configured model, if any.</param>
/// <param name="HasApiKey">Whether an API key is stored. The key is never returned.</param>
/// <param name="IsConfigured">
/// True when enabling the judge would actually do something. Turning it on without a
/// provider would silently degrade every save into a heuristic guess.
/// </param>
/// <param name="TimeoutSeconds">Configured initial attempt timeout, if specified.</param>
public readonly record struct SemanticJudgeView(
    bool Enabled,
    string? BaseUrl,
    string? Model,
    bool HasApiKey,
    bool IsConfigured,
    int? TimeoutSeconds = null);
