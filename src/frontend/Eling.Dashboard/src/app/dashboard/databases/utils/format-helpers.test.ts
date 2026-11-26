import { describe, it, expect } from "vitest"
import { cacheBadgeCopy, formatCacheAge, formatCacheExpiry, formatColumnType } from "./format-helpers"

describe("formatColumnType", () => {
  it("appends the declared length to a bare type", () => {
    expect(formatColumnType("varchar", 50)).toBe("varchar(50)")
    expect(formatColumnType("char", 4)).toBe("char(4)")
  })

  it("leaves a type alone when it carries no length", () => {
    expect(formatColumnType("datetime", null)).toBe("datetime")
    expect(formatColumnType("int", undefined)).toBe("int")
    expect(formatColumnType("text", 0)).toBe("text")
    expect(formatColumnType(undefined, 50)).toBe("")
  })

  it("does not add a second length to a type that already has one", () => {
    // SQLite reports the whole declared type as one string.
    expect(formatColumnType("varchar(50)", 50)).toBe("varchar(50)")
  })
})

describe("formatCacheAge", () => {
  const now = Date.now()
  const oneMinAgo = new Date(now - 60 * 1000).toISOString()
  const oneHourAgo = new Date(now - 60 * 60 * 1000).toISOString()
  const oneDayAgo = new Date(now - 24 * 60 * 60 * 1000).toISOString()

  it("formats relative time", () => {
    expect(formatCacheAge(new Date(now - 1000).toISOString())).toBe("just now")
    expect(formatCacheAge(oneMinAgo)).toBe("1 min ago")
    expect(formatCacheAge(oneHourAgo)).toBe("1 h ago")
    expect(formatCacheAge(oneDayAgo)).toBe("1 day ago")
  })

  it("handles null/undefined", () => {
    expect(formatCacheAge(null)).toBe("cached")
    expect(formatCacheAge(undefined)).toBe("cached")
  })
})

describe("cacheBadgeCopy", () => {
  it("still reports cache facts for a live read with no cachedAt", () => {
    const cache = cacheBadgeCopy(null, 7 * 24 * 60 * 60)
    expect(cache.age).toBe("just now")
    expect(cache.expiry).toMatch(/expires in (6d|7d)/)
  })

  it("uses the timestamp and TTL the response carries", () => {
    const cachedAt = new Date(Date.now() - 60 * 1000).toISOString()
    const cache = cacheBadgeCopy(cachedAt, 2 * 24 * 60 * 60)
    expect(cache.age).toBe("1 min ago")
    expect(cache.expiry).toMatch(/expires in 1d/)
  })
})

describe("formatCacheExpiry", () => {
  it("shows remaining time with default 7-day TTL", () => {
    const now = Date.now()
    const oneDayAgo = new Date(now - 24 * 60 * 60 * 1000).toISOString()
    const sixDaysAgo = new Date(now - 6 * 24 * 60 * 60 * 1000).toISOString()
    const eightDaysAgo = new Date(now - 8 * 24 * 60 * 60 * 1000).toISOString()

    expect(formatCacheExpiry(oneDayAgo)).toMatch(/expires in 5d|6d/)
    expect(formatCacheExpiry(sixDaysAgo)).toMatch(/expires in (1d|23h|0d)/)
    expect(formatCacheExpiry(eightDaysAgo)).toBe("expired")
  })

  it("handles custom TTL", () => {
    const now = Date.now()
    const thirtyMinAgo = new Date(now - 30 * 60 * 1000).toISOString()
    const result = formatCacheExpiry(thirtyMinAgo, 3600)
    expect(result).toContain("expires in")
    expect(result).toMatch(/expires in (29|30)m/)
  })

  it("handles null/undefined", () => {
    expect(formatCacheExpiry(null)).toBe("")
    expect(formatCacheExpiry(undefined)).toBe("")
  })
})
