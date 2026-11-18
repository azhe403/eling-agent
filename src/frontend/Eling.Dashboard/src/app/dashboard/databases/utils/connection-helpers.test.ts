import { describe, it, expect } from "vitest"
import type { DbConn } from "../types"
import { deriveConnectionName, providerLabel, isPostgreSql } from "./connection-helpers"

describe("deriveConnectionName PostgreSql", () => {
  it("omits the port suffix on 5432", () => {
    expect(deriveConnectionName("PostgreSql", "pg.internal", "5432", "orders", "")).toBe("orders@pg.internal")
  })
  it("appends a non-default port", () => {
    expect(deriveConnectionName("PostgreSql", "pg.internal", "5433", "orders", "")).toBe("orders@pg.internal:5433")
  })
  it("keeps the server-level form without a database", () => {
    expect(deriveConnectionName("PostgreSql", "pg.internal", "5432", "", "")).toBe("pg.internal")
  })
  it("keeps the MySql default port semantics", () => {
    expect(deriveConnectionName("MySql", "mysql.internal", "3306", "orders", "")).toBe("orders@mysql.internal")
    expect(deriveConnectionName("MySql", "mysql.internal", "3307", "orders", "")).toBe("orders@mysql.internal:3307")
  })
  it("uses the Redis default port for the suffix", () => {
    expect(deriveConnectionName("Redis", "redis.internal", "6379", "", "")).toBe("redis.internal")
    expect(deriveConnectionName("Redis", "redis.internal", "6380", "", "")).toBe("redis.internal:6380")
  })
})

describe("providerLabel", () => {
  it("labels PostgreSql as PostgreSQL", () => {
    expect(providerLabel("PostgreSql")).toBe("PostgreSQL")
  })
})

describe("isPostgreSql", () => {
  it("is true for PostgreSql and false for others", () => {
    expect(isPostgreSql({ provider: "PostgreSql" } as DbConn)).toBe(true)
    expect(isPostgreSql({ provider: "MySql" } as DbConn)).toBe(false)
    expect(isPostgreSql({ provider: "Sqlite" } as DbConn)).toBe(false)
    expect(isPostgreSql(null)).toBe(false)
    expect(isPostgreSql(undefined)).toBe(false)
  })
})
