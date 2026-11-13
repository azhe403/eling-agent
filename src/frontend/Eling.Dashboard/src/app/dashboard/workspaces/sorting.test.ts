import { describe, expect, it } from "vitest"

import type { WorkspaceGroup } from "./aggregate"
import { parseDirection, parseSort, sortWorkspaces, type IndexStatus } from "./sorting"

/**
 * The order the workspaces page lists its cards in. Each key is checked
 * against the rows it is supposed to answer for, in both directions, and
 * against the two rules that are easy to get wrong: a row with no reading
 * never claims a place it has no right to, and equal keys fall back to the
 * name instead of registry order.
 */

function ws(
  root: string,
  extra: Partial<WorkspaceGroup> = {},
): WorkspaceGroup {
  return {
    root,
    processes: [],
    running: false,
    totalMemoryBytes: null,
    totalCpuPercent: null,
    lastHeartbeat: "2026-01-01T00:00:00.000Z",
    codebaseEnabled: true,
    transport: null,
    ownerPort: null,
    ...extra,
  }
}

const names = (groups: WorkspaceGroup[]) => groups.map((g) => g.root)

describe("parseSort", () => {
  it("falls back to the default for a missing or unknown value", () => {
    expect(parseSort(null)).toBe("name")
    expect(parseSort("")).toBe("name")
    expect(parseSort("bogus")).toBe("name")
  })

  it("accepts every key it offers", () => {
    expect(parseSort("memory")).toBe("memory")
    expect(parseSort("indexed")).toBe("indexed")
  })
})

describe("parseDirection", () => {
  it("falls back to the natural order for a missing or unknown value", () => {
    expect(parseDirection(null)).toBe("natural")
    expect(parseDirection("")).toBe("natural")
    expect(parseDirection("bogus")).toBe("natural")
  })

  it("accepts the flipped order", () => {
    expect(parseDirection("flipped")).toBe("flipped")
  })
})

describe("sortWorkspaces", () => {
  it("orders by name, case-insensitively and with numbers read as numbers", () => {
    const groups = [ws("/w/beta"), ws("/w/alpha"), ws("/w/Alpha2"), ws("/w/Alpha10")]
    expect(names(sortWorkspaces(groups, "name"))).toEqual([
      "/w/alpha",
      "/w/Alpha2",
      "/w/Alpha10",
      "/w/beta",
    ])
  })

  it("mirrors the name order when flipped", () => {
    const groups = [ws("/w/beta"), ws("/w/alpha"), ws("/w/Alpha2")]
    const natural = names(sortWorkspaces(groups, "name"))
    expect(names(sortWorkspaces(groups, "name", "flipped"))).toEqual(
      [...natural].reverse(),
    )
  })

  it("never mutates the list it was given", () => {
    const groups = [ws("/w/b"), ws("/w/a")]
    sortWorkspaces(groups, "name")
    expect(names(groups)).toEqual(["/w/b", "/w/a"])
  })

  it("puts the most recently seen first, and the least when flipped", () => {
    const groups = [
      ws("/w/old", { lastHeartbeat: "2026-01-01T00:00:00.000Z" }),
      ws("/w/new", { lastHeartbeat: "2026-01-03T00:00:00.000Z" }),
      ws("/w/mid", { lastHeartbeat: "2026-01-02T00:00:00.000Z" }),
    ]
    expect(names(sortWorkspaces(groups, "active"))).toEqual([
      "/w/new",
      "/w/mid",
      "/w/old",
    ])
    expect(names(sortWorkspaces(groups, "active", "flipped"))).toEqual([
      "/w/old",
      "/w/mid",
      "/w/new",
    ])
  })

  it("puts running workspaces first, then by name — reversed when flipped", () => {
    const groups = [
      ws("/w/zeta"),
      ws("/w/beta", { running: true }),
      ws("/w/alpha", { running: true }),
      ws("/w/abc"),
    ]
    // natural: the running group on top, then offline, each alphabetical.
    expect(names(sortWorkspaces(groups, "status"))).toEqual([
      "/w/alpha",
      "/w/beta",
      "/w/abc",
      "/w/zeta",
    ])
    // flipped: the offline group on top, the name order within a group runs
    // in its own direction, so the two read alpha-to-z either way.
    expect(names(sortWorkspaces(groups, "status", "flipped"))).toEqual([
      "/w/abc",
      "/w/zeta",
      "/w/alpha",
      "/w/beta",
    ])
  })

  it("puts the largest working set first and unknown readings last", () => {
    const groups = [
      ws("/w/unknown"),
      ws("/w/small", { totalMemoryBytes: 10 }),
      ws("/w/large", { totalMemoryBytes: 900 }),
    ]
    expect(names(sortWorkspaces(groups, "memory"))).toEqual([
      "/w/large",
      "/w/small",
      "/w/unknown",
    ])
  })

  it("flips the known readings but keeps unknown readings last", () => {
    const groups = [
      ws("/w/unknown"),
      ws("/w/small", { totalMemoryBytes: 10 }),
      ws("/w/large", { totalMemoryBytes: 900 }),
    ]
    expect(names(sortWorkspaces(groups, "memory", "flipped"))).toEqual([
      "/w/small",
      "/w/large",
      "/w/unknown",
    ])
  })

  it("puts the busiest first and unknown readings last", () => {
    const groups = [
      ws("/w/unknown"),
      ws("/w/idle", { totalCpuPercent: 0 }),
      ws("/w/busy", { totalCpuPercent: 42 }),
    ]
    expect(names(sortWorkspaces(groups, "cpu"))).toEqual([
      "/w/busy",
      "/w/idle",
      "/w/unknown",
    ])
  })

  it("flips the CPU order but keeps unknown readings last", () => {
    const groups = [
      ws("/w/unknown"),
      ws("/w/idle", { totalCpuPercent: 0 }),
      ws("/w/busy", { totalCpuPercent: 42 }),
    ]
    expect(names(sortWorkspaces(groups, "cpu", "flipped"))).toEqual([
      "/w/idle",
      "/w/busy",
      "/w/unknown",
    ])
  })

  it("puts the most recently indexed first, never-indexed last", () => {
    const groups = [ws("/w/never"), ws("/w/old"), ws("/w/new"), ws("/w/absent")]
    const statuses: Record<string, IndexStatus> = {
      "/w/old": { lastIndexedAt: "2026-01-01T00:00:00.000Z" },
      "/w/new": { lastIndexedAt: "2026-02-01T00:00:00.000Z" },
      "/w/never": { lastIndexedAt: null },
    }
    const order = sortWorkspaces(groups, "indexed", "natural", (root) => statuses[root])
    // Both never-indexed rows share a key, so the name decides between them.
    expect(names(order)).toEqual(["/w/new", "/w/old", "/w/absent", "/w/never"])
  })

  it("flips the indexed order but keeps never-indexed last", () => {
    const groups = [ws("/w/never"), ws("/w/old"), ws("/w/new"), ws("/w/absent")]
    const statuses: Record<string, IndexStatus> = {
      "/w/old": { lastIndexedAt: "2026-01-01T00:00:00.000Z" },
      "/w/new": { lastIndexedAt: "2026-02-01T00:00:00.000Z" },
      "/w/never": { lastIndexedAt: null },
    }
    const order = sortWorkspaces(groups, "indexed", "flipped", (root) => statuses[root])
    expect(names(order)).toEqual(["/w/old", "/w/new", "/w/absent", "/w/never"])
  })

  it("breaks ties by name, so equal keys never depend on registry order", () => {
    const groups = [
      ws("/w/charlie", { totalCpuPercent: 5 }),
      ws("/w/bravo", { totalCpuPercent: 5 }),
      ws("/w/alpha", { totalCpuPercent: 5 }),
    ]
    expect(names(sortWorkspaces(groups, "cpu"))).toEqual([
      "/w/alpha",
      "/w/bravo",
      "/w/charlie",
    ])
    expect(names(sortWorkspaces(groups, "cpu", "flipped"))).toEqual([
      "/w/alpha",
      "/w/bravo",
      "/w/charlie",
    ])
  })
})
