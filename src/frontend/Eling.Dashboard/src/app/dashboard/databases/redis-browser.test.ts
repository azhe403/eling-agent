import { describe, it, expect, vi, beforeEach, afterEach } from "vitest"

import {
  dbRowLabel,
  dbRowCount,
  dbRowAsOf,
  parseRedisDb,
  pickRedisDb,
  fetchRedisDatabases,
  type RedisDbInfo,
} from "./redis-browser"

describe("dbRowLabel", () => {
  it("reads db3", () => {
    expect(dbRowLabel({ index: 3, keys: 3 })).toBe("db3")
  })
})

describe("dbRowCount", () => {
  it("reads 3 keys", () => {
    expect(dbRowCount({ index: 3, keys: 3 })).toBe("3 keys")
  })

  it("reads 0 keys", () => {
    expect(dbRowCount({ index: 0, keys: 0 })).toBe("0 keys")
  })
})

describe("dbRowAsOf", () => {
  it("says nothing for a live count", () => {
    expect(dbRowAsOf({ index: 3, keys: 3 })).toBeNull()
  })

  it("stamps a cached count with the moment it was taken", () => {
    const at = "2026-10-27T08:15:00.000Z"
    expect(dbRowAsOf({ index: 3, keys: 3, fromCache: true, cachedAt: at })).toBe(
      `as of ${new Date(at).toLocaleString()}`,
    )
  })

  it("still says cached when the stamp is missing", () => {
    expect(dbRowAsOf({ index: 3, keys: 3, fromCache: true, cachedAt: null })).toBe("cached")
  })

  it("ignores an unparseable stamp", () => {
    expect(dbRowAsOf({ index: 3, keys: 3, fromCache: true, cachedAt: "not a date" })).toBe("cached")
  })
})

describe("parseRedisDb", () => {
  it("parses a valid index", () => {
    expect(parseRedisDb("3")).toBe(3)
  })

  it("rejects a non-number", () => {
    expect(parseRedisDb("abc")).toBeNull()
  })

  it("rejects a negative index", () => {
    expect(parseRedisDb("-1")).toBeNull()
  })

  it("rejects an out-of-range index", () => {
    expect(parseRedisDb("99")).toBeNull()
  })

  it("rejects null", () => {
    expect(parseRedisDb(null)).toBeNull()
  })
})

describe("pickRedisDb", () => {
  const available: RedisDbInfo[] = [
    { index: 3, keys: 3 },
    { index: 5, keys: 128 },
  ]

  it("uses the URL index when present", () => {
    expect(
      pickRedisDb({ urlDb: "5", savedTab: { connId: "a", redisDb: 3 }, connId: "a", available }),
    ).toBe(5)
  })

  it("uses the saved index for the same connection", () => {
    expect(pickRedisDb({ urlDb: null, savedTab: { connId: "a", redisDb: 3 }, connId: "a", available })).toBe(3)
  })

  it("ignores a saved index from another connection", () => {
    // available[0] must not be the saved index, or the case passes whether or
    // not the saved tab leaked in.
    const other: RedisDbInfo[] = [{ index: 7, keys: 1 }]
    expect(pickRedisDb({ urlDb: null, savedTab: { connId: "b", redisDb: 3 }, connId: "a", available: other })).toBe(7)
  })

  it("uses the first database that holds keys when nothing else names one", () => {
    expect(pickRedisDb({ urlDb: null, savedTab: null, connId: "a", available })).toBe(3)
  })

  it("falls back to zero when nothing is available", () => {
    expect(pickRedisDb({ urlDb: null, savedTab: null, connId: "a", available: [] })).toBe(0)
  })
})

describe("fetchRedisDatabases", () => {
  const originalFetch = globalThis.fetch

  beforeEach(() => {
    vi.restoreAllMocks()
  })

  afterEach(() => {
    globalThis.fetch = originalFetch
  })

  it("resolves with the parsed list on 200", async () => {
    globalThis.fetch = vi.fn(
      async () =>
        new Response(JSON.stringify([{ index: 3, keys: 3 }]), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
    )
    const result = await fetchRedisDatabases("c1")
    expect(result).toEqual([{ index: 3, keys: 3, fromCache: false, cachedAt: null }])
  })

  it("carries the cache stamp an offline server sends", async () => {
    const cachedAt = "2026-10-27T08:15:00.000Z"
    globalThis.fetch = vi.fn(
      async () =>
        new Response(
          JSON.stringify([{ index: 3, keys: 3, fromCache: true, cachedAt }]),
          { status: 200, headers: { "content-type": "application/json" } },
        ),
    )

    const result = await fetchRedisDatabases("c1")

    expect(result[0]?.fromCache).toBe(true)
    expect(result[0]?.cachedAt).toBe(cachedAt)
  })

  it("rejects on a non-OK response", async () => {
    globalThis.fetch = vi.fn(async () => new Response("nope", { status: 400 }))
    await expect(fetchRedisDatabases("c1")).rejects.toThrow()
  })
})
