using System.Collections.Generic;

namespace Eling.Desktop.Models;

/// <summary>
/// The workspace tree's open/closed shape, as the backend serves it. Both
/// clients read and write this one document, so they render the same tree.
/// </summary>
/// <param name="Expanded">Node ids the user explicitly opened.</param>
/// <param name="Collapsed">Node ids the user explicitly closed.</param>
public sealed record WorkspaceExpansionDto(
    IReadOnlyList<string> Expanded,
    IReadOnlyList<string> Collapsed);
