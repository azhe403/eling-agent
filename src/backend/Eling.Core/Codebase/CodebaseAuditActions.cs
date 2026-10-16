namespace Eling.Core.Codebase;

/// <summary>
/// The canonical action names the codebase domain records, spelled exactly as
/// the tool surface amendment B1 lists them, so filtering the trail by action
/// reads back as "this tool ran" with no translation table (design §3). Only
/// the explicit invocations are here: the background watcher calls
/// <see cref="CodebaseIndexService.IndexAsync"/> directly and is deliberately
/// unrecorded (design §12 — a watcher refresh is system self-noise, in the
/// same class as a sweep that prunes nothing).
/// </summary>
internal static class CodebaseAuditActions
{
    public const string Index = "codebase_index";

    public const string Search = "codebase_search";

    public const string Status = "codebase_status";
}
