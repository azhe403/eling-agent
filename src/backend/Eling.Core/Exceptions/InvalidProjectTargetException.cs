namespace Eling.Core.Exceptions;

/// <summary>
/// Thrown when an explicit ancestor-project target cannot be honored: either the
/// workspace has no own <c>.eling</c> scope yet, or the requested name does not
/// match any ancestor scope in the chain. Explicit ancestor targeting never
/// creates a scope; it only reaches levels that already exist.
/// </summary>
public sealed class InvalidProjectTargetException(
    string projectName,
    string reason,
    IReadOnlyList<string> availableProjectNames)
    : InvalidOperationException(BuildMessage(projectName, reason, availableProjectNames))
{
    public string ProjectName { get; } = projectName;

    public string Reason { get; } = reason;

    public IReadOnlyList<string> AvailableProjectNames { get; } = availableProjectNames;

    private static string BuildMessage(string projectName, string reason, IReadOnlyList<string> available)
    {
        var availableText = available.Count == 0 ? "(none)" : string.Join(", ", available);
        return $"Cannot target project '{projectName}': {reason}. Available ancestor scopes: {availableText}.";
    }
}
