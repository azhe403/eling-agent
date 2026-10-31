/**
 * The order the workspaces page lists its cards in.
 *
 * The registry's own order is the order a process happened to register, which
 * is not an answer to any question a reader has. Six keys are offered instead —
 * name, last active, running first, memory, CPU, last indexed — and each can
 * run in either direction: the direction flips the whole order, including the
 * name that decides ties, so the reversed list is the mirror of the forward
 * one and no two rows ever swap places between renders for the same input.
 *
 * Nothing here touches React: same input, same output. The order lives in the
 * URL as `?sort=&dir=`, so a shared link and the back button land on the same
 * list. Both params are optional; absent means the default, which keeps
 * shared links clean.
 */

import { workspaceName } from "@/components/workspaces/runtime-rows"
import type { WorkspaceGroup } from "./aggregate"

export const SORT_KEYS = [
  "name",
  "active",
  "status",
  "memory",
  "cpu",
  "indexed",
] as const

export type SortKey = (typeof SORT_KEYS)[number]

/**
 * The way the reader's list runs relative to a key's own headline order.
 * A key has a natural order (name: A→Z; active: newest first; running: live
 * first; a metric: biggest first) and that is what the page shows for the
 * key — and `natural` says "keep it". `flipped` mirrors the whole list.
 *
 * The name is not "ascending"/"descending" on purpose: those words mean
 * different things per key ("active ascending" is newest-first, not oldest),
 * and what the reader controls is a mirror, not a universal direction.
 */
export type SortDirection = "natural" | "flipped"

/** The order every key starts in. Left out of the URL, so shared links stay clean. */
export const DEFAULT_DIRECTION: SortDirection = "natural"

export function parseDirection(value: string | null): SortDirection {
  return value === "flipped" ? "flipped" : DEFAULT_DIRECTION
}

/** The default. It is left out of the URL, so shared links stay clean. */
export const DEFAULT_SORT: SortKey = "name"

export function parseSort(value: string | null): SortKey {
  return SORT_KEYS.includes(value as SortKey) ? (value as SortKey) : DEFAULT_SORT
}

/** The order of the segmented control, and the words it shows. */
export const SORT_COPY: Record<SortKey, { label: string; title: string }> = {
  name: {
    label: "Name",
    title: "Alphabetical by workspace name, A to Z",
  },
  active: {
    label: "Active",
    title: "Most recently seen first — the latest heartbeat at the top",
  },
  status: {
    label: "Running",
    title: "Running workspaces first, then the offline ones",
  },
  memory: {
    label: "RAM",
    title: "Largest working set first",
  },
  cpu: {
    label: "CPU",
    title: "Busiest first; 100% means every core busy",
  },
  indexed: {
    label: "Indexed",
    title: "Most recently indexed first; never-indexed workspaces last",
  },
}

/**
 * The codebase index of one workspace, as far as the order is concerned.
 * Only `lastIndexedAt` is read; a workspace with no entry is never indexed.
 */
export type IndexStatus = { lastIndexedAt: string | null }

function byName(a: WorkspaceGroup, b: WorkspaceGroup): number {
  return workspaceName(a.root).localeCompare(workspaceName(b.root), undefined, {
    sensitivity: "base",
    numeric: true,
  })
}

/** A timestamp as a sortable number; a missing or unreadable one sorts lowest. */
function timeOf(iso: string | null | undefined): number {
  if (!iso) return Number.NEGATIVE_INFINITY
  const ms = Date.parse(iso)
  return Number.isNaN(ms) ? Number.NEGATIVE_INFINITY : ms
}

/**
 * The value each key is ranked by, and whether the value is present.
 *
 * - `name` and `status` always have a value: the rank is the value itself,
 *   so the whole reading runs in one direction or the other.
 * - `active`, `memory`, `cpu`, `indexed` can lack a value. The present
 *   readings run in the reader's chosen direction, and the missing ones
 *   keep the very bottom of the list in either direction: "no reading" is
 *   never "the smallest", and a workspace whose process has no metric yet
 *   does not claim the top of a list ranked by RAM.
 */
const KEYS: Record<
  SortKey,
  {
    value: (g: WorkspaceGroup, statusOf: (root: string) => IndexStatus | undefined) => number
    has: (g: WorkspaceGroup, statusOf: (root: string) => IndexStatus | undefined) => boolean
  }
> = {
  name: {
    // A rank that is never compared arithmetically: the value is only used to
    // pick the sign, and `byName` is the real ordering that runs below it.
    value: () => 0,
    has: () => true,
  },
  active: {
    value: (g) => timeOf(g.lastHeartbeat),
    has: (g) => timeOf(g.lastHeartbeat) !== Number.NEGATIVE_INFINITY,
  },
  status: {
    value: (g) => Number(g.running),
    has: () => true,
  },
  memory: {
    value: (g) => g.totalMemoryBytes ?? Number.NaN,
    has: (g) => g.totalMemoryBytes != null,
  },
  cpu: {
    value: (g) => g.totalCpuPercent ?? Number.NaN,
    has: (g) => g.totalCpuPercent != null,
  },
  indexed: {
    value: (g, statusOf) => timeOf(statusOf(g.root)?.lastIndexedAt),
    has: (g, statusOf) => timeOf(statusOf(g.root)?.lastIndexedAt) !== Number.NEGATIVE_INFINITY,
  },
}

/**
 * One step of the final comparison: whether `a` reads *better* than `b` on
 * the chosen key, in the reader's direction, with unknown readings never
 * claiming a place they have no right to.
 */
function rankAbove(
  a: WorkspaceGroup,
  b: WorkspaceGroup,
  sort: SortKey,
  flip: 1 | -1,
  statusOf: (root: string) => IndexStatus | undefined,
): number {
  const { value, has } = KEYS[sort]
  if (sort === "name") {
    // The direction runs the whole A-to-Z reading one way or the other.
    return flip * byName(a, b)
  }

  const va = value(a, statusOf)
  const vb = value(b, statusOf)
  const aHas = has(a, statusOf)
  const bHas = has(b, statusOf)

  if (aHas && bHas) {
    // Both have a reading. For every key here the value is written "bigger
    // is better" (the natural order is the bigger value on top), so the
    // natural direction runs the bigger one first (vb - va), and
    // `flipped` mirrors that:
    //   active   newest heartbeat at the top
    //   status   running before offline
    //   memory   biggest working set at the top
    //   cpu      busiest at the top
    //   indexed  most recently indexed at the top
    return flip === 1 ? vb - va : va - vb
  }
  if (aHas) return -1
  if (bHas) return 1
  return 0
}

/**
 * The same rows, in the requested order. The input array is never mutated:
 * the caller's `groups` state is the source of truth for every other count on
 * the page, and a sort that reordered it in place would change them all.
 *
 * The reader's direction chooses which end of the reading is the top:
 * `natural` runs every key with its head on top (the biggest RAM first, the
 * newest heartbeat first, running before offline, A before Z) and
 * `flipped` runs the reading the other way. Two subtleties keep the order
 * honest either way: the name tiebreak always runs A-to-Z — it only
 * separates rows the key has already tied, so it cannot undo the direction
 * the reader picked — and a reading some workspaces lack (no live metric,
 * never indexed) is unknown, not zero: it sits below every known reading,
 * because “no reading” is never “the smallest”.
 *
 * `statusOf` is keyed the way the page keys its status map: lower-cased root.
 */
export function sortWorkspaces(
  groups: readonly WorkspaceGroup[],
  sort: SortKey,
  direction: SortDirection = DEFAULT_DIRECTION,
  statusOf: (root: string) => IndexStatus | undefined = () => undefined,
): WorkspaceGroup[] {
  // The value of every key is written "bigger is better" (the natural order
  // has the head on top), so `natural` runs with the value ascending and
  // `flipped` with it descending.
  const flip: 1 | -1 = direction === "flipped" ? -1 : 1
  return [...groups].sort(
    (a, b) => rankAbove(a, b, sort, flip, statusOf) || byName(a, b),
  )
}
