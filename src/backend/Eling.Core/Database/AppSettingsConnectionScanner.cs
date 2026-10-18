using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Eling.Core.Database;

public sealed class AppSettingsConnectionScanner
{
    private readonly string _filePath;
    private readonly ILogger<AppSettingsConnectionScanner>? _logger;

    public AppSettingsConnectionScanner(
        string filePath,
        ILogger<AppSettingsConnectionScanner>? logger = null)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public IReadOnlyList<DetectedConnection> Scan()
    {
        var results = new List<DetectedConnection>();

        var files = ResolveFiles();
        foreach (var file in files)
        {
            ScanFile(file, results);
        }

        return results;
    }

    private IEnumerable<string> ResolveFiles()
    {
        if (Directory.Exists(_filePath))
        {
            // Scan every appsettings*.json in the directory (appsettings.json,
            // appsettings.Development.json, appsettings.Staging.json, ...).
            return Directory.EnumerateFiles(_filePath, "appsettings*.json")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (File.Exists(_filePath))
        {
            return new[] { _filePath };
        }

        return Array.Empty<string>();
    }

    private void ScanFile(string filePath, List<DetectedConnection> results)
    {
        JsonDocument doc;
        try
        {
            var json = File.ReadAllText(filePath);
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse appsettings at {Path}.", filePath);
            return;
        }

        using (doc)
        {
            // Standard ASP.NET Core section: { "ConnectionStrings": { "Key": "conn str" } }
            if (doc.RootElement.TryGetProperty("ConnectionStrings", out var cs) &&
                cs.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in cs.EnumerateObject())
                {
                    // Skip duplicates already added from another appsettings file.
                    if (results.Any(r => string.Equals(r.SettingsKey, prop.Name, StringComparison.Ordinal)))
                    {
                        continue;
                    }
                    AddCandidate(results, prop.Name, prop.Value);
                }
            }

            // Nested connection-string objects at any depth.
            ScanNested(doc.RootElement, results, string.Empty);
        }
    }

    private static void ScanNested(JsonElement element, List<DetectedConnection> results, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // If this object directly carries a ConnectionString, treat it as a candidate.
        if (TryGetCaseInsensitive(element, "ConnectionString", out var csProp) &&
            csProp.ValueKind == JsonValueKind.String)
        {
            var key = string.IsNullOrWhiteSpace(path) ? "ConnectionString" : path;
            // Avoid duplicates already added by the standard ConnectionStrings section.
            if (!results.Any(r => string.Equals(r.SettingsKey, key, StringComparison.Ordinal)))
            {
                results.Add(Classify(key, csProp.GetString() ?? string.Empty));
            }
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, "ConnectionStrings", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(prop.Name, "ConnectionString", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var childPath = string.IsNullOrWhiteSpace(path)
                ? prop.Name
                : path + "." + prop.Name;

            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                ScanNested(prop.Value, results, childPath);
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                var idx = 0;
                foreach (var item in prop.Value.EnumerateArray())
                {
                    ScanNested(item, results, childPath + "[" + idx + "]");
                    idx++;
                }
            }
        }
    }

    private static void AddCandidate(List<DetectedConnection> results, string key, JsonElement valueElement)
    {
        if (valueElement.ValueKind != JsonValueKind.String)
        {
            results.Add(new DetectedConnection(
                key,
                key,
                string.Empty,
                "***",
                null,
                "value is not a string"));
            return;
        }

        var value = valueElement.GetString() ?? string.Empty;
        results.Add(Classify(key, value));
    }

    private static DetectedConnection Classify(string key, string value)
    {
        DatabaseProvider? provider = null;
        string? rejection = null;
        string name = key;
        string masked = "***";
        bool classified = false;

        try
        {
            var mb = new MySqlConnectionStringBuilder(value);
            if (!string.IsNullOrWhiteSpace(mb.Server) && !string.IsNullOrWhiteSpace(mb.Database))
            {
                provider = DatabaseProvider.MySql;
                name = mb.Port != 3306
                    ? $"{mb.Database}@{mb.Server}:{mb.Port}"
                    : $"{mb.Database}@{mb.Server}";
                var mBuilder = new MySqlConnectionStringBuilder(value) { Password = "***" };
                masked = mBuilder.ToString();
                classified = true;
            }
        }
        catch
        {
        }

        if (!classified)
        {
            try
            {
                var sb = new SqliteConnectionStringBuilder(value);
                if (!string.IsNullOrWhiteSpace(sb.DataSource))
                {
                    provider = DatabaseProvider.Sqlite;
                    var ds = sb.DataSource;
                    if (ds.Equals(":memory:", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(ds))
                    {
                        name = key;
                    }
                    else
                    {
                        name = $"{Path.GetFileNameWithoutExtension(ds)}@local";
                    }
                    var sBuilder = new SqliteConnectionStringBuilder(value);
                    masked = sBuilder.ToString();
                    classified = true;
                }
            }
            catch
            {
            }
        }

        if (!classified)
        {
            provider = null;
            rejection = "unrecognized connection string";
            name = key;
            masked = "***";
        }

        return new DetectedConnection(key, name, value, masked, provider, rejection);
    }

    private static bool TryGetCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
