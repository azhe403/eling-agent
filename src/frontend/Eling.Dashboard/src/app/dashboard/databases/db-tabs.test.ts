import { describe, it, expect } from "vitest"

import { REDIS_TAB_TABLE, isRedisTab, redisTabKey, tabKey, persistableTab, sanitizePersistedTab } from "./db-tabs"

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
describe("persistableTab", () => {
  it("drops browseLimit and browsePage but keeps the committed filter, sort and manual condition", () => {
    const t = {
      connId: "c1",
      table: "orders",
      database: "db",
      browseLimit: 500,
      browsePage: 3,
      browseFilters: [{ column: "name", op: "contains" as const, value: "a", enabled: true, logic: "first" as const }],
      browseSort: [{ column: "updated_at", order: "desc" as const }],
      manualCondition: "is_active = 1",
      filterOpen: true,
    }
    expect(persistableTab(t)).toEqual({
      connId: "c1",
      table: "orders",
      database: "db",
      browseFilters: t.browseFilters,
      browseSort: t.browseSort,
      manualCondition: "is_active = 1",
      filterOpen: true,
    })
  })
  it("keeps redis identity fields", () => {
    const t = { connId: "c1", table: "Redis keys", kind: "redis" as const, redisDb: 2, browseLimit: 250, browsePage: 2 }
    expect(persistableTab(t)).toEqual({ connId: "c1", table: "Redis keys", kind: "redis", redisDb: 2 })
  })
})
describe("sanitizePersistedTab", () => {
  it("round-trips a persisted tab through persistableTab", () => {
    const t = {
      connId: "c1",
      table: "orders",
      database: "db",
      browseFilters: [{ column: "name", op: "eq" as const, value: "a", enabled: false, logic: "and" as const }],
      browseSort: [{ column: "id", order: "asc" as const }],
      manualCondition: "x = 1",
      manualEnabled: false,
      filterOpen: true,
    }
    const restored = sanitizePersistedTab(JSON.parse(JSON.stringify(persistableTab(t))))
    expect(restored).toEqual(t)
  })
  it("drops unknown operators and tolerates garbage shapes", () => {
    const restored = sanitizePersistedTab({
      connId: "c1",
      table: "orders",
      browseFilters: [
        { column: "id", op: "bogus", value: "1", enabled: true, logic: "first" },
        "nope",
        { column: "id", op: "gt", value: 42, enabled: "yes", logic: "OR" },
      ],
      browseSort: [{ column: "id", order: "sideways" }, { column: "id", order: "desc" }, 7],
      manualCondition: 123,
    })
    expect(restored).toEqual({
      connId: "c1",
      table: "orders",
      browseFilters: [{ column: "id", op: "gt", value: "42", enabled: true, logic: "or" }],
      browseSort: [{ column: "id", order: "desc" }],
      manualCondition: "",
    })
  })
  it("returns null when the identity fields are missing", () => {
    expect(sanitizePersistedTab({ table: "orders" })).toBeNull()
    expect(sanitizePersistedTab(null)).toBeNull()
    expect(sanitizePersistedTab("orders")).toBeNull()
  })
})
