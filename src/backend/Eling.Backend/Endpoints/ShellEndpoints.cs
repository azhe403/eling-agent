using Eling.Backend.Shell;
using Eling.Core.Shell;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Endpoints;

/// <summary>
/// Dashboard read/write access to the global <c>shell_execute</c> configuration.
/// The PUT is a full replacement: the body is the complete configuration, not a
/// patch. Rules whose path/pattern is blank are dropped by the store, and a
/// rule whose <c>Enabled</c> changed is stamped with the change time so the UI
/// can show when it was last touched.
/// </summary>
public static class ShellEndpoints
{
    public static WebApplication MapShellEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/shell");

        group.MapGet("/config", (ShellConfigStore store) => TypedResults.Ok(store.GetConfig()));

        group.MapPut("/config", (ShellConfigStore store, ShellConfig req, ILogger<ShellConfigStore> logger) =>
        {
            if (req.DefaultTimeoutSeconds < 1)
            {
                return Results.BadRequest("defaultTimeoutSeconds must be at least 1.");
            }

            if (req.MaxOutputBytes < 1)
            {
                return Results.BadRequest("maxOutputBytes must be at least 1.");
            }

            // The store's Update re-reads the file under its gate and pins the
            // top-level UpdatedAt itself, so the delta is computed against the
            // snapshot it hands us instead of a second GetConfig read.
            try
            {
                var persisted = store.Update(current => PackWithStamp(current, req));
                return TypedResults.Ok(persisted);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "PUT /api/shell/config could not persist to {FileName}", "shell-config.json");
                return Results.Problem(statusCode: 500, title: "Could not save the shell configuration.", detail: ex.Message);
            }
        });

        return app;
    }

    private static ShellConfig PackWithStamp(ShellConfig current, ShellConfig req)
    {
        var now = DateTimeOffset.UtcNow;

        return new ShellConfig(
            enabled: req.Enabled,
            restrictToAllowedDirectories: req.RestrictToAllowedDirectories,
            allowedDirectories: (req.AllowedDirectories ?? [])
                .Where(rule => rule is not null)
                .Select(rule =>
                {
                    var path = rule.Path?.Trim() ?? "";
                    return new AllowedDirectoryRule(
                        path: path,
                        enabled: rule.Enabled,
                        createdAt: rule.CreatedAt,
                        updatedAt: ChangedStamp(current.AllowedDirectories, existing => existing.Path, path, rule.Enabled, rule.UpdatedAt, now));
                })
                .ToList(),
            blockedCommands: (req.BlockedCommands ?? [])
                .Where(rule => rule is not null)
                .Select(rule =>
                {
                    var pattern = rule.Pattern?.Trim() ?? "";
                    return new BlockedCommandRule(
                        pattern: pattern,
                        enabled: rule.Enabled,
                        createdAt: rule.CreatedAt,
                        updatedAt: ChangedStamp(current.BlockedCommands, existing => existing.Pattern, pattern, rule.Enabled, rule.UpdatedAt, now));
                })
                .ToList(),
            rootGuardrail: req.RootGuardrail,
            defaultTimeoutSeconds: req.DefaultTimeoutSeconds,
            maxOutputBytes: req.MaxOutputBytes);
    }

    /// <summary>
    /// The rule's <c>UpdatedAt</c>: the change time when this rule existed
    /// before with the opposite <c>Enabled</c>, otherwise whatever the client
    /// sent. Matching is by path/pattern, case-insensitively — never by list
    /// position, because the body may add, drop or reorder rules.
    /// </summary>
    private static DateTimeOffset? ChangedStamp<T>(
        List<T> existing,
        Func<T, string> key,
        string value,
        bool enabled,
        DateTimeOffset? provided,
        DateTimeOffset now)
        where T : ShellRuleBase
    {
        var previous = existing.FirstOrDefault(rule => string.Equals(key(rule), value, StringComparison.OrdinalIgnoreCase));
        return previous is not null && previous.Enabled != enabled ? now : provided;
    }
}
