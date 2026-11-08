/**
 * Connection helpers: deriving names, building payloads, validating ports.
 *
 * These are pure functions, so they can be unit-tested without a browser.
 */

import type { DbConn, ManualPayload } from "../types"

/** The name a connection gets when nobody typed one. */
export const deriveConnectionName = (
  provider: string,
  host: string,
  port: string,
  database: string,
  filePath: string,
): string => {
  if (provider === "Sqlite") {
    const file = filePath.trim().split(/[\\/]/).filter(Boolean).pop() ?? ""
    const base = file.replace(/\.[^.]+$/, "")
    return base ? `${base}@local` : ""
  }
  const h = host.trim()
  if (!h) return ""
  const p = port.trim()
  const portSuffix = p && Number(p) !== 3306 ? `:${p}` : ""
  const db = database.trim()
  return db ? `${db}@${h}${portSuffix}` : `${h}${portSuffix}`
}

/**
 * Whether a connection is server-level: it names no database, so it reaches
 * every database on the server.
 */
export const isServerLevel = (provider: string, database: string): boolean =>
  provider !== "Sqlite" && database.trim() === ""

/** The fields a person typed, in the shape the backend builds a string from. */
export const manualPayload = (
  provider: string,
  host: string,
  port: string,
  database: string,
  username: string,
  password: string,
  filePath: string,
): ManualPayload =>
  provider === "Sqlite"
    ? { filePath: filePath.trim() || null, host: null, port: null, database: null, username: null, password: null }
    : {
        host: host.trim() || null,
        port: port.trim() ? Number(port.trim()) : null,
        database: database.trim() || null,
        username: username.trim() || null,
        password: password.length > 0 ? password : null,
        filePath: null,
      }

/** True when the port is a number a socket can actually use. */
export const isValidPort = (port: string): boolean => {
  const trimmed = port.trim()
  if (!trimmed) return true
  if (!/^\d+$/.test(trimmed)) return false
  const value = Number(trimmed)
  return value >= 1 && value <= 65535
}

/** A value short enough to sit next to its column name in a toast. */
export const shortValue = (value: string): string =>
  value.length > 120 ? `${value.slice(0, 120)}…` : value

/** The provider identifier for a connection. */
export const providerLabel = (provider: string): string => {
  switch (provider) {
    case "Sqlite": return "SQLite"
    case "MySql": return "MySQL"
    case "Redis": return "Redis"
    default: return provider
  }
}

/** Whether a connection is Redis. */
export const isRedis = (conn: DbConn | null | undefined): boolean =>
  conn?.provider === "Redis"

/** Whether a connection is MySQL. */
export const isMySql = (conn: DbConn | null | undefined): boolean =>
  conn?.provider === "MySql"

/** The quote character for an identifier on a connection. */
export const quoteChar = (conn: DbConn | null | undefined): string =>
  isMySql(conn) ? "`" : '"'

/** Quote an identifier for a connection. */
export const quoteIdent = (name: string, conn: DbConn | null | undefined): string => {
  const q = quoteChar(conn)
  return q + name.replace(new RegExp(q, "g"), q + q) + q
}

/** Quote a value literal for SQL. */
export const sqlQuote = (v: unknown): string => {
  if (v === null || v === undefined) return "NULL"
  if (typeof v === "number" && Number.isFinite(v)) return String(v)
  if (typeof v === "boolean") return v ? "1" : "0"
  const s = String(v)
  if (/^-?\d+(\.\d+)?$/.test(s)) return s
  return "'" + s.replace(/'/g, "''") + "'"
}

/** Build a WHERE clause from a row's primary key values. */
export const buildWherePk = (
  row: unknown[],
  columns: string[],
  pkColumnNames: string[],
): string => {
  if (pkColumnNames.length === 0) return ""
  return pkColumnNames
    .map((pk) => {
      const idx = columns.indexOf(pk)
      const v = row[idx]
      return `${pk} = ${sqlQuote(v)}`
    })
    .join(" AND ")
}

/** Parse an API error message into a human-friendly string. */
export const parseApiError = (text: string): string => {
  const trimmed = text.trim()
  if (!trimmed) return "Request failed."
  try {
    const j = JSON.parse(trimmed)
    const msg = j?.detail || j?.title || j?.message || j?.error
    if (typeof msg === "string" && msg.length > 0) {
      const lower = msg.toLowerCase()
      if (lower.includes("mutations not allowed on read-only")) {
        return "This connection is read-only, so data changes are disabled."
      }
      if (lower.includes("not a mutation statement")) {
        return "Only INSERT / UPDATE / DELETE statements are allowed here."
      }
      if (lower.includes("no primary key")) {
        return "This table has no primary key, so rows cannot be edited."
      }
      return msg
    }
  } catch {
    // not JSON — fall through to raw text
  }
  return trimmed.length > 200 ? trimmed.slice(0, 200) + "…" : trimmed
}

/** The API base path. */
export const API = "/api/databases"