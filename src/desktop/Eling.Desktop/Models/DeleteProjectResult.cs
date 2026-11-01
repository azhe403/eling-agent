using System.Collections.Generic;

namespace Eling.Desktop.Models;

/// <summary>
/// The outcome of a workspace delete: whether everything was removed, and
/// what was not.
/// </summary>
/// <remarks>
/// The backend reports per component — the registry row, the codebase index,
/// the local <c>.eling</c> folder — and any of them can fail. A delete that
/// left the codebase index on disk is not a success, so the caller needs the
/// reason rather than a bare boolean.
/// </remarks>
public sealed record DeleteProjectResult(
    bool Succeeded,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The warnings as one line, for a status bar that shows one.</summary>
    public string WarningText => string.Join(" ", Warnings);
}
