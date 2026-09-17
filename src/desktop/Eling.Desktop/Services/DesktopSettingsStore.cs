using System;
using System.IO;
using System.Text.Json;
using Eling.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Eling.Desktop.Services;

public sealed class DesktopSettingsStore(ILogger<DesktopSettingsStore> logger)
{
    private static JsonSerializerOptions JsonOptions => JsonDefaults.Shared;

    public string? GetBackendUrl()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path))
            {
                return null;
            }

            var doc = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(path), JsonOptions);
            return string.IsNullOrWhiteSpace(doc?.BackendUrl) ? null : doc.BackendUrl;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read desktop settings");
            return null;
        }
    }

    public void SetBackendUrl(string? backendUrl)
    {
        try
        {
            var dir = SettingsDirectory();
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "settings.json"),
                JsonSerializer.Serialize(new SettingsDocument(backendUrl), JsonOptions));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save desktop settings");
        }
    }

    private static string SettingsDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "eling-desktop");
    }

    private static string SettingsPath() => Path.Combine(SettingsDirectory(), "settings.json");

    private sealed record SettingsDocument(string? BackendUrl);
}
