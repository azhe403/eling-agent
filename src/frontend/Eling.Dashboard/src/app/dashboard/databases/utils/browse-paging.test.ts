import { describe, it, expect } from "vitest"
import {
  BROWSE_LIMIT_OPTIONS,
  DEFAULT_BROWSE_LIMIT,
  effectiveBrowseLimit,
  effectiveBrowsePage,
  defaultBrowseLimit,
  totalPages,
  clampPage,
} from "./browse-paging"

describe("effectiveBrowseLimit", () => {
  it("returns 100 without a tab or field", () => {
    expect(effectiveBrowseLimit(null)).toBe(100)
    expect(effectiveBrowseLimit({ connId: "c", table: "t" })).toBe(100)
  })
  it("honours a tab's limit", () => {
    expect(effectiveBrowseLimit({ connId: "c", table: "t", browseLimit: 500 })).toBe(500)
  })
  it("falls back to 100 for garbage (0, NaN, negative, non-member)", () => {
    expect(effectiveBrowseLimit({ connId: "c", table: "t", browseLimit: 0 })).toBe(100)
    expect(effectiveBrowseLimit({ connId: "c", table: "t", browseLimit: Number.NaN })).toBe(100)
    expect(effectiveBrowseLimit({ connId: "c", table: "t", browseLimit: -50 })).toBe(100)
    expect(effectiveBrowseLimit({ connId: "c", table: "t", browseLimit: 7 })).toBe(100)
  })
  it("every option is accepted", () => {
    for (const n of BROWSE_LIMIT_OPTIONS) {
      expect(effectiveBrowseLimit({ connId: "c", table: "t", browseLimit: n })).toBe(n)
    }
  })
})

describe("effectiveBrowsePage", () => {
  it("returns 1 by default and clamps garbage to 1", () => {
    expect(effectiveBrowsePage(null)).toBe(1)
    expect(effectiveBrowsePage({ connId: "c", table: "t" })).toBe(1)
    expect(effectiveBrowsePage({ connId: "c", table: "t", browsePage: 0 })).toBe(1)
    expect(effectiveBrowsePage({ connId: "c", table: "t", browsePage: -3 })).toBe(1)
    expect(effectiveBrowsePage({ connId: "c", table: "t", browsePage: 3.7 })).toBe(3)
    expect(effectiveBrowsePage({ connId: "c", table: "t", browsePage: 3 })).toBe(3)
  })
})

describe("defaultBrowseLimit (mode of open table tabs)", () => {
  const tab = (browseLimit?: number, kind?: "redis") => ({
    connId: "c",
    table: kind === "redis" ? "Redis keys" : "t",
    kind,
    browseLimit,
  })
  it("returns 100 with no tabs", () => {
    expect(defaultBrowseLimit([])).toBe(100)
  })
  it("picks the most frequent effective limit", () => {
    expect(defaultBrowseLimit([tab(100), tab(500), tab(100), tab(500), tab(500)])).toBe(500)
  })
  it("counts a missing browseLimit as 100", () => {
    expect(defaultBrowseLimit([tab(), tab(), tab(500)])).toBe(100)
  })
  it("breaks ties toward the larger value", () => {
    expect(defaultBrowseLimit([tab(100), tab(250), tab(500), tab(250), tab(500)])).toBe(500)
  })
  it("ignores Redis tabs", () => {
    expect(defaultBrowseLimit([tab(500, "redis"), tab(100), tab(100)])).toBe(100)
  })
})

describe("totalPages", () => {
  it("is null when total is unknown", () => {
    expect(totalPages(null, 100)).toBeNull()
  })
  it("rounds up", () => {
    expect(totalPages(250, 100)).toBe(3)
    expect(totalPages(0, 100)).toBe(0)
    expect(totalPages(100, 100)).toBe(1)
  })
})

describe("clampPage", () => {
  it("keeps a valid page", () => {
    expect(clampPage(2, 250, 100)).toBe(2)
  })
  it("pins to the last page when out of range", () => {
    expect(clampPage(99, 250, 100)).toBe(3)
  })
  it("pins to 1 when the table is empty or total unknown", () => {
    expect(clampPage(5, 0, 100)).toBe(1)
    expect(clampPage(5, null, 100)).toBe(1)
  })
  it("pins sub-1 pages to 1", () => {
    expect(clampPage(0, 250, 100)).toBe(1)
  })
})

describe("constants", () => {
  it("options are exactly the spec values and default is 100", () => {
    expect([...BROWSE_LIMIT_OPTIONS]).toEqual([50, 100, 250, 500, 1000])
    expect(DEFAULT_BROWSE_LIMIT).toBe(100)
  })
})
