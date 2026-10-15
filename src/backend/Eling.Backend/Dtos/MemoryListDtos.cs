namespace Eling.Backend.Dtos;

/// <summary>
/// Paging metadata for a memory list. Two counts travel with the window: the
/// size of the whole selection, and how many survive the request's filters —
/// so a client never guesses either number from the page it received.
/// </summary>
/// <param name="Total">
/// How many memories the selection covers before any filter — the scope size.
/// </param>
/// <param name="Matching">
/// How many the selection matches after the request's status/type/query
/// filters, before offset/limit — the count the client should show as
/// "showing".
/// </param>
/// <param name="Limit">The page size the caller asked for, if any.</param>
/// <param name="Offset">The offset the caller asked for, if any.</param>
public sealed record MemoryListMetadata(
    int Total,
    int Matching,
    int? Limit,
    int? Offset);

/// <summary>
/// The memory list envelope: <see cref="Metadata"/> plus the
/// <see cref="Items"/> for the requested window.
/// </summary>
public sealed record MemoryListDto(
    MemoryListMetadata Metadata,
    IReadOnlyCollection<ScopedMemoryDto> Items);
