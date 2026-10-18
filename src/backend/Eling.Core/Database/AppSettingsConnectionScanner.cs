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
        if (!File.Exists(_filePath))
        {
            return results;
        }

        JsonDocument doc;
        try
        {
            var json = File.ReadAllText(_filePath);
            doc = JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse appsettings at {Path}.", _filePath);
            return results;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("ConnectionStrings", out var cs) ||
                cs.ValueKind != JsonValueKind.Object)
            {
                return results;
            }

            foreach (var prop in cs.EnumerateObject())
            {
                var key = prop.Name;
                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    results.Add(new DetectedConnection(
                        key,
                        key,
                        string.Empty,
                        "***",
                        null,
                        "value is not a string"));
                    continue;
                }

                var value = prop.Value.GetString() ?? string.Empty;

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

                results.Add(new DetectedConnection(key, name, value, masked, provider, rejection));
            }
        }

        return results;
    }
}
