namespace Eling.Core.Memory;

/// <summary>
/// The canonical action names the memory domain records, spelled exactly as the
/// tool surface amendment B1 lists them, so filtering the trail by action reads
/// back as "this tool ran" with no translation table (design §3). Only the
/// operations <see cref="ScopedMemoryService"/> actually performs are here; the
/// remaining memory tools (<c>memory_init_project</c>, <c>memory_rebuild_index</c>,
/// <c>memory_maintenance</c>, <c>memory_recall</c>, <c>memory_project_status</c>)
/// belong to their own choke points and keep their own names.
/// </summary>
internal static class MemoryAuditActions
{
    /// <summary>
    /// A create or a smart-save merge alike — <see cref="SaveAction"/> tells the
    /// two apart, and the entry's before/after snapshots show which it was (B1).
    /// </summary>
    public const string Save = "memory_save";

    public const string Delete = "memory_delete";

    /// <summary>
    /// Copy into a project scope. Its <c>operation=move</c> is the old
    /// <c>memory_move</c>: one action, and the source snapshot tells the two apart.
    /// </summary>
    public const string CopyToProject = "memory_copy_to_project";

    public const string PromoteToGlobal = "memory_promote_to_global";

    public const string Get = "memory_get";

    public const string List = "memory_list";

    public const string Search = "memory_search";
}
