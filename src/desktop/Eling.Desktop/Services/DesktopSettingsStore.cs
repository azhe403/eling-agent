using System;
using System.IO;
using System.Text.Json;
using Eling.Core.Scope;
using Eling.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Eling.Desktop.Services;

public sealed class DesktopSettingsStore(ILogger<DesktopSettingsStore> logger)
{
    private const string FileName = "desktop-settings.json";

    private static JsonSerializerOptions JsonOptions => JsonDefaults.Shared;

    public const double MinWindowWidth = 400;
    public const double MinWindowHeight = 300;

    public string? GetBackendUrl()
    {
        return ReadDocument()?.BackendUrl is { } backendUrl &&
            !string.IsNullOrWhiteSpace(backendUrl)
            ? backendUrl
            : null;
    }

    public void SetBackendUrl(string? backendUrl)
    {
        try
        {
            var current = ReadDocument();
            WriteDocument(current with { BackendUrl = backendUrl });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save desktop settings");
        }
    }

    public WindowBounds? GetWindowBounds()
    {
        try
        {
            var window = ReadDocument()?.Window;
            return window is not null && IsValid(window) ? window : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read window bounds");
            return null;
        }
    }

    public void SaveWindowBounds(WindowBounds bounds)
    {
        try
        {
            if (!IsValid(bounds))
            {
                return;
            }

            var current = ReadDocument();
            WriteDocument(current with { Window = bounds });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save window bounds");
        }
    }

    private static bool IsValid(WindowBounds bounds) =>
        double.IsFinite(bounds.X) &&
        double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) &&
        double.IsFinite(bounds.Height) &&
        bounds.Width >= MinWindowWidth &&
        bounds.Height >= MinWindowHeight;

    private SettingsDocument ReadDocument()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path))
            {
                return new SettingsDocument(null, null);
            }

            return JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(path), JsonOptions)
                ?? new SettingsDocument(null, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read desktop settings");
            return new SettingsDocument(null, null);
        }
    }

    private static void WriteDocument(SettingsDocument document)
    {
        var dir = SettingsDirectory();
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            SettingsPath(),
            JsonSerializer.Serialize(document, JsonOptions));
    }

    private static string SettingsDirectory()
    {
        // Project convention: all user config lives under ~/.config/eling.
        // Honors ELING_USER_SCOPE like the backend (ProjectContext).
        var userScope = UserScope.Resolve(Environment.GetEnvironmentVariable("ELING_USER_SCOPE"));
        return userScope.ConfigDirectory;
    }

    private static string SettingsPath() => Path.Combine(SettingsDirectory(), FileName);

    private sealed record SettingsDocument(string? BackendUrl, WindowBounds? Window);

    public sealed record WindowBounds(double X, double Y, double Width, double Height, bool IsMaximized);
}
