using System.Text.Json;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Judging;

/// <summary>
/// Reads and writes the semantic judge configuration from
/// <c>&lt;user-scope&gt;/config/semantic-judge.json</c>.
/// </summary>
/// <remarks>
/// Follows <c>JsonProjectScopePolicyStore</c>: camelCase, indented, nulls omitted,
/// best-effort IO that degrades to "not configured" rather than failing a save. A null
/// or empty <c>apiKey</c> in an update means "leave the stored key alone", so a settings
/// form can round-trip without ever having to send the secret back.
/// </remarks>
public sealed class SemanticJudgeStore
{
    private const string FileName = "semantic-judge.json";

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly ILogger<SemanticJudgeStore> _logger;

    private SemanticJudgeConfig _config = new();

    public SemanticJudgeStore(UserScope userScope, ILogger<SemanticJudgeStore> logger)
    {
        ArgumentNullException.ThrowIfNull(userScope);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _filePath = Path.Combine(userScope.ConfigDirectory, FileName);
        Load();
        EnsureTemplate();
    }

    /// <summary>
    /// The on-disk starting point. Deliberately complete-but-empty rather than omitted
    /// fields: with <c>WhenWritingNull</c> an all-null config would serialise to
    /// <c>{"enabled":false}</c>, which tells whoever opens the file nothing about which
    /// keys it wants. Empty strings keep every key visible and still read as
    /// "not configured", because the view treats whitespace as absent.
    /// </summary>
    private static readonly SemanticJudgeConfig Template = new()
    {
        Enabled = false,
        BaseUrl = string.Empty,
        Model = string.Empty,
        ApiKey = string.Empty,
        TimeoutSeconds = 10,
    };

    /// <summary>
    /// Writes the template when the file is absent — on first run, and again if it is
    /// deleted. Only when absent: a file that exists but is corrupt is left untouched so
    /// it can be inspected or repaired rather than silently overwritten.
    /// </summary>
    private void EnsureTemplate()
    {
        if (File.Exists(_filePath))
        {
            return;
        }

        Write(Template);
        _logger.LogInformation("Created semantic judge config template at {Path}", _filePath);
    }

    public SemanticJudgeView GetView()
    {
        lock (_gate)
        {
            return ToView(_config);
        }
    }

    /// <summary>
    /// Merges a partial update. Nulls and an empty <paramref name="apiKey"/> leave the
    /// stored value unchanged.
    /// </summary>
    public SemanticJudgeView Update(
        bool? enabled = null,
        string? baseUrl = null,
        string? model = null,
        string? apiKey = null,
        int? timeoutSeconds = null)
    {
        lock (_gate)
        {
            _config = _config with
            {
                Enabled = enabled ?? _config.Enabled,
                BaseUrl = baseUrl is not null ? baseUrl : _config.BaseUrl,
                Model = model is not null ? model : _config.Model,
                ApiKey = !string.IsNullOrEmpty(apiKey) ? apiKey : _config.ApiKey,
                TimeoutSeconds = timeoutSeconds ?? _config.TimeoutSeconds,
            };

            Save();
            return ToView(_config);
        }
    }

    /// <summary>
    /// Resolves the live configuration. Returns <c>false</c> unless the judge is both
    /// enabled and fully configured, which is what keeps the save path on its historical
    /// heuristic behaviour when the feature is off.
    /// </summary>
    public bool TryGetConfig(out string baseUrl, out string model, out string apiKey)
        => TryGetConfig(out baseUrl, out model, out apiKey, out _);

    /// <summary>
    /// Resolves the live configuration including any custom initial timeout in seconds.
    /// </summary>
    public bool TryGetConfig(
        out string baseUrl,
        out string model,
        out string apiKey,
        out int? timeoutSeconds)
    {
        lock (_gate)
        {
            var view = ToView(_config);
            if (!view.IsConfigured)
            {
                baseUrl = string.Empty;
                model = string.Empty;
                apiKey = string.Empty;
                timeoutSeconds = null;
                return false;
            }

            baseUrl = _config.BaseUrl!;
            model = _config.Model!;
            apiKey = _config.ApiKey ?? string.Empty;
            timeoutSeconds = _config.TimeoutSeconds;
            return true;
        }
    }

    private static SemanticJudgeView ToView(SemanticJudgeConfig config)
    {
        var hasKey = !string.IsNullOrWhiteSpace(config.ApiKey);
        var hasEndpoint = !string.IsNullOrWhiteSpace(config.BaseUrl);
        var hasModel = !string.IsNullOrWhiteSpace(config.Model);

        return new SemanticJudgeView(
            config.Enabled,
            config.BaseUrl,
            config.Model,
            hasKey,
            config.Enabled && hasKey && hasEndpoint && hasModel,
            config.TimeoutSeconds);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var config = JsonSerializer.Deserialize<SemanticJudgeConfig>(
                File.ReadAllText(_filePath),
                SemanticJudgeJsonContext.Default.SemanticJudgeConfig);

            if (config is not null)
            {
                _config = config;
            }
        }
        catch (Exception ex)
        {
            // A corrupt config must not take the backend down: fall back to
            // "not configured", which restores the historical heuristic save path.
            _logger.LogWarning(ex, "Failed to read semantic judge config from {Path}", _filePath);
        }
    }

    private void Save() => Write(_config);

    private void Write(SemanticJudgeConfig config)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                _filePath,
                JsonSerializer.Serialize(config, SemanticJudgeJsonContext.Default.SemanticJudgeConfig));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write semantic judge config to {Path}", _filePath);
        }
    }
}
