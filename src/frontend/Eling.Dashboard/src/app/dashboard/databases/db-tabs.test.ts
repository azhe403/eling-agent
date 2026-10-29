import { describe, it, expect } from "vitest"

import { REDIS_TAB_TABLE, isRedisTab, redisTabKey, tabKey } from "./db-tabs"

describe("isRedisTab", () => {
  it("is true for a tab flagged as Redis", () => {
    expect(isRedisTab({ kind: "redis" })).toBe(true)
  })

  it("is true for a tab carrying the Redis label", () => {
    expect(isRedisTab({ table: REDIS_TAB_TABLE })).toBe(true)
  })

  it("is false for an ordinary table tab", () => {
    expect(isRedisTab({ table: "orders" })).toBe(false)
  })

  it("is false for null", () => {
    expect(isRedisTab(null)).toBe(false)
  })
})

describe("redisTabKey", () => {
  it("differs for two databases of one connection", () => {
    const db3 = { connId: "c1", table: REDIS_TAB_TABLE, kind: "redis" as const, redisDb: 3 }
    const db5 = { connId: "c1", table: REDIS_TAB_TABLE, kind: "redis" as const, redisDb: 5 }
    expect(redisTabKey(db3)).not.toBe(redisTabKey(db5))
  })

  it("differs across connections", () => {
    const a = { connId: "c1", table: REDIS_TAB_TABLE, kind: "redis" as const, redisDb: 3 }
    const b = { connId: "c2", table: REDIS_TAB_TABLE, kind: "redis" as const, redisDb: 3 }
    expect(redisTabKey(a)).not.toBe(redisTabKey(b))
  })

  it("defaults a missing redisDb to zero", () => {
    expect(redisTabKey({ connId: "c1", table: REDIS_TAB_TABLE })).toBe("c1:0")
  })
})

describe("tabKey", () => {
  it("keeps a SQL tab apart from the Redis tab of the same connection", () => {
    // The bug this guards: a Redis tab with no database and a SQL tab with no
    // database both reduce to "c1:0", so opening one hid the other.
    const redis = { connId: "c1", table: REDIS_TAB_TABLE, kind: "redis" as const, redisDb: 0 }
    const sql = { connId: "c1", table: "orders", database: null }

    expect(tabKey(redis)).not.toBe(tabKey(sql))
  })

  it("matches a Redis tab that carries only the table name", () => {
    // A tab restored from the URL has no kind.
    expect(tabKey({ connId: "c1", table: REDIS_TAB_TABLE, redisDb: 3 })).toBe(tabKey({ connId: "c1", table: REDIS_TAB_TABLE, kind: "redis", redisDb: 3 }))
  })

  it("keeps two SQL tables of one connection apart", () => {
    expect(tabKey({ connId: "c1", table: "orders" })).not.toBe(tabKey({ connId: "c1", table: "items" }))
  })
})