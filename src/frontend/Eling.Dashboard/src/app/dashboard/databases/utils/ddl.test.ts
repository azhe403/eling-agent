import { describe, expect, it } from "vitest"

import { ddlFromQueryResult, ddlFromSchema, ddlSqlFor, quoteIdentifier, quoteLiteral } from "./ddl"
import type { TableSchema } from "../types"

describe("ddlSqlFor", () => {
  it("uses SHOW CREATE TABLE for MySQL and escapes the identifier", () => {
    expect(ddlSqlFor("MySql", "we`ird")).toBe("SHOW CREATE TABLE `we``ird`")
  })

  it("reads sqlite_master for SQLite and escapes the literal", () => {
    expect(ddlSqlFor("Sqlite", "o'brien")).toContain("name = 'o''brien'")
  })

  it("has no native statement for PostgreSQL", () => {
    expect(ddlSqlFor("PostgreSql", "t")).toBeNull()
  })
})

describe("ddlFromQueryResult", () => {
  it("takes the second column of a MySQL SHOW CREATE row", () => {
    expect(
      ddlFromQueryResult("MySql", {
        columns: ["Table", "Create Table"],
        rows: [["t", "CREATE TABLE `t` (`id` int)"]],
        truncated: false,
      })
    ).toBe("CREATE TABLE `t` (`id` int)")
  })

  it("takes the first column of a SQLite sqlite_master row", () => {
    expect(
      ddlFromQueryResult("Sqlite", {
        columns: ["sql"],
        rows: [["CREATE TABLE t (id int)"]],
        truncated: false,
      })
    ).toBe("CREATE TABLE t (id int)")
  })
})

const table: TableSchema = {
  name: "users",
  kind: "table",
  columns: [
    { name: "id", dataType: "integer", isNullable: false, isPrimaryKey: true },
    { name: "name", dataType: "varchar", isNullable: true, isPrimaryKey: false, maxLength: 50 },
  ],
  foreignKeys: [
    { column: "team_id", referencesTable: "teams", referencesColumn: "id" },
  ],
  indexes: [{ name: "ix_users_name", isUnique: true, columns: ["name"] }],
}

describe("ddlFromSchema", () => {
  it("builds a CREATE statement with types, key and indexes", () => {
    const ddl = ddlFromSchema("PostgreSql", table)
    expect(ddl).toContain('CREATE TABLE "users"')
    expect(ddl).toContain('"name" varchar(50)')
    expect(ddl).toContain('PRIMARY KEY ("id")')
    expect(ddl).toContain('CONSTRAINT "fk_users_team_id" FOREIGN KEY ("team_id") REFERENCES "teams" ("id")')
    expect(ddl).toContain('CREATE UNIQUE INDEX "ix_users_name" ON "users" ("name")')
  })

  it("does not pretend a view definition is available", () => {
    expect(ddlFromSchema("PostgreSql", { ...table, kind: "view" })).toContain("not part of the schema snapshot")
  })
})

describe("quoting", () => {
  it("quotes identifiers in the provider's dialect", () => {
    expect(quoteIdentifier("MySql", "a`b")).toBe("`a``b`")
    expect(quoteIdentifier("PostgreSql", 'a"b')).toBe('"a""b"')
  })

  it("quotes string literals", () => {
    expect(quoteLiteral("o'brien")).toBe("'o''brien'")
  })
})
