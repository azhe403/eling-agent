namespace Eling.Backend.Bootstrap;

/// <summary>
/// What the reconcile loop should do about the frontend dev server on port 4427.
/// </summary>
internal enum FrontendAction
{
    /// <summary>The dashboard is already serving. Leave it alone.</summary>
    None,

    /// <summary>Nothing holds port 4427. Spawn the frontend dev server.</summary>
    Start,

    /// <summary>
    /// Something holds port 4427 but does not respond. That process is hung, so
    /// kill it and spawn a fresh one.
    /// </summary>
    Restart,
}