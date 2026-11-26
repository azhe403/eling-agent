/**
 * DDL for the table on screen.
 *
 * MySQL and SQLite can print their own CREATE statement, so the exact text is
 * read through the existing read-only /query endpoint. PostgreSQL has no such
 * statement, so its DDL is reconstructed from the schema snapshot the page
 * already holds: column types, primary key, foreign keys and indexes.
 */

import type { Provider, QueryResult, TableSchema } from "../types"
import { formatColumnType } from "./format-helpers"

/** A table or column name, quoted in the dialect that will read it. */
export const quoteIdentifier = (provider: Provider, name: string): string =>
  provider === "MySql"
    ? `\`${name.replace(/`/g, "``")}\``
    : `"${name.replace(/"/g, '""')}"`

/** A string literal, with the quote escaped so a table name cannot break the statement. */
export const quoteLiteral = (value: string): string => `'${value.replace(/'/g, "''")}'`

/**
 * The statement that prints the DDL, or null when the provider has none.
 * `SHOW CREATE TABLE` is already classified read-only by SqlSafety.
 */
export const ddlSqlFor = (provider: Provider, table: string): string | null => {
  if (provider === "MySql") return `SHOW CREATE TABLE ${quoteIdentifier(provider, table)}`
  if (provider === "Sqlite") {
    return `SELECT sql FROM sqlite_master WHERE type IN ('table', 'view') AND name = ${quoteLiteral(table)}`
  }
  return null
}

/** The DDL text in a /query answer: MySQL returns [table, statement], SQLite [statement]. */
export const ddlFromQueryResult = (provider: Provider, result: QueryResult): string => {
  const row = result.rows[0]
  if (!row) return ""
  const value = provider === "MySql" ? row[1] : row[0]
  return value === null || value === undefined ? "" : String(value)
}

/** Reconstruct a CREATE TABLE statement from a schema snapshot (PostgreSQL). */
export const ddlFromSchema = (provider: Provider, table: TableSchema): string => {
  const q = (name: string) => quoteIdentifier(provider, name)
  if (table.kind === "view") {
    return "-- A view's defining query is not part of the schema snapshot."
  }

  const lines: string[] = []
  for (const col of table.columns ?? []) {
    const type = formatColumnType(col.dataType, col.maxLength) || "text"
    lines.push(`    ${q(col.name)} ${type}${col.isNullable ? "" : " NOT NULL"}`)
  }
  const primaryKey = (table.columns ?? []).filter((c) => c.isPrimaryKey).map((c) => q(c.name))
  if (primaryKey.length > 0) lines.push(`    PRIMARY KEY (${primaryKey.join(", ")})`)
  for (const fk of table.foreignKeys ?? []) {
    // The frontend schema carries the columns, not the backend's constraint
    // name, so name it the way the schema view already does for SQLite.
    const fkName = `fk_${table.name}_${fk.column}`
    lines.push(
      `    CONSTRAINT ${q(fkName)} FOREIGN KEY (${q(fk.column)}) REFERENCES ${q(fk.referencesTable)} (${q(fk.referencesColumn)})`
    )
  }

  const body = lines.length > 0 ? `\n${lines.join(",\n")}\n` : "\n"
  const statements = [`CREATE TABLE ${q(table.name)} (${body});`]
  for (const index of table.indexes ?? []) {
    statements.push(
      `CREATE ${index.isUnique ? "UNIQUE " : ""}INDEX ${q(index.name)} ON ${q(table.name)} (${index.columns.map((c) => q(c)).join(", ")});`
    )
  }
  return statements.join("\n\n")
}
