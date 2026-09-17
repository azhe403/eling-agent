using System.Text.Json;
using Eling.Backend.Dtos;
using Eling.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Agent.Services;

public sealed class ProviderStore
{
    private const string FileName = "agent-provider.json";

    private static JsonSerializerOptions JsonOptions => JsonDefaults.Shared;

    private readonly string _filePath;
    private readonly ILogger<ProviderStore> _logger;
    private readonly object _gate = new();

    private string? _baseUrl;
    private string? _model;
    private string? _apiKey;
    private List<string> _modelsCached = [];

    public ProviderStore(string dataDir, ILogger<ProviderStore> logger, string? legacyDataDir = null)
    {
        _logger = logger;
        _filePath = Path.Combine(dataDir, FileName);
        if (!string.IsNullOrWhiteSpace(legacyDataDir))
        {
            StoreMigration.MoveFileIfNeeded(_filePath, Path.Combine(legacyDataDir, FileName), _logger, "provider config");
        }

        Load();
    }

    public ProviderView GetView()
    {
        lock (_gate)
        {
            return new ProviderView(_baseUrl, _model, !string.IsNullOrEmpty(_apiKey), new List<string>(_modelsCached));
        }
    }

    public void Update(string? baseUrl, string? model, string? apiKey)
    {
        lock (_gate)
        {
            if (baseUrl is not null)
            {
                _baseUrl = baseUrl;
            }

            if (model is not null)
            {
                _model = model;
            }

            if (!string.IsNullOrEmpty(apiKey))
            {
                _apiKey = apiKey;
            }

            Save();
        }
    }

    public bool TryGetConfig(out string baseUrl, out string? model, out string? apiKey)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl))
            {
                baseUrl = string.Empty;
                model = null;
                apiKey = null;
                return false;
            }

            baseUrl = _baseUrl;
            model = _model;
            apiKey = _apiKey;
            return true;
        }
    }

    internal void SetModelsCached(IReadOnlyList<string> models)
    {
        lock (_gate)
        {
            _modelsCached = new List<string>(models);
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var doc = JsonSerializer.Deserialize<ProviderDocument>(json, JsonOptions);
            if (doc is null)
            {
                return;
            }

            _baseUrl = doc.BaseUrl;
            _model = doc.Model;
            _apiKey = doc.ApiKey;
            _modelsCached = doc.ModelsCached ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load provider config from {Path}", _filePath);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var doc = new ProviderDocument(_baseUrl, _model, _apiKey, _modelsCached);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(doc, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save provider config to {Path}", _filePath);
        }
    }

    private sealed record ProviderDocument(string? BaseUrl, string? Model, string? ApiKey, List<string>? ModelsCached);
}
