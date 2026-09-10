# Recall Minimum Slot per Level - Design
Date: 2026-09-09
Status: Approved for implementation (guarantee 1 + interleave remainder)
Scope: Eling.Core (ScopedMemoryService.SearchAsync, merged scope + limit path)
> Paths in examples are anonymized placeholders (`C:\work\acme\integrations\payments`).

## Problem

Merged recall/search from a child scope builds results **level-grouped** (own
root block, then ancestors, then global) and then applies `Take(limit)` from
the front. While the head level produces >= `limit` hits, ancestor and global
hits are silently starved, even when they are far more relevant than the head's
tail hits. Observed: a near-identical dummy memory saved in the ancestor scope
(`AZ`) never surfaced in recall (default limit 10) because the head level
(`Eling`) filled all 10 slots with loose OR-fallback matches.

Root cause is structural, not a ranking bug: no cross-level relevance exists,
and the head block always consumes the whole quota.

## Design

When `ScopedMemoryService.SearchAsync(query, scope, limit)` runs with
`scope = merged` (or `all`) and `limit > 0` truncates the result, replace the
plain `Take(limit)` with a **round-robin interleave across levels**:

1. Group the level-grouped results by level, preserving first-appearance order
   (already nearest-first: own root, ancestors, then global).
2. Fill slots in passes: each pass takes the next unconsumed result from every
   level that still has results, nearest level first, until `limit` is reached
   or all levels are exhausted.

Properties:

- **Guarantee >= 1 slot per level**: any level (own, ancestor, global) that has
  at least one hit is guaranteed a slot whenever `limit` allows, so a crowded
  head can never starve an ancestor/global hit entirely.
- **Multiple ancestor hits surface**: a level with several relevant hits keeps
  receiving one slot per pass; it is not capped at one.
- **Nearest-first preserved within each pass**: the own root is always first in
  a pass, so a head with many matches still leads overall; the change only stops
  the head from monopolising the whole quota.
- **No empty slots**: a level with zero hits contributes nothing and consumes
  no quota.
- **Unchanged when no truncation**: when the merged result already fits the
  limit (or no limit is given), the level-grouped order is returned untouched;
  N=1 and `project`/`global` scopes behave exactly as the old `Take`.

No interface or merger changes. `MemoryMerger` keeps its level-grouped
contract for full (untruncated) results; this spec amends only the truncation
step in `ScopedMemoryService.SearchAsync`.

## Example

Levels: child 18 hits, parent 3 hits, global 2 hits; limit 10.

Result order: `[C1,P1,G1, C2,P2,G2, C3,P3, C4,C5]` -> child 5, parent 3,
global 2.

## Testing

- `MemoryRecallServiceChainTests`: ancestor and global hits appear in recall
  when the head level fills the limit; a parent level with two matches yields
  both; a level with no matching hits leaves no empty slots (recall count stays
  at the limit).
- Full `Eling.Core.Tests` suite as regression gate.

## Out of scope

- Raising the default `recallLimit` (kept at 10; token cost per recalled memory
  is the full content).
- Cross-level relevance re-ranking (would need score normalisation across
  per-scope FTS5 indexes).
- Dashboard/UI parity for the new ordering.
