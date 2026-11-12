/**
 * Central shared types for the databases explorer.
 * All other files in this directory should import from this rather than
 * duplicating or importing from db-tabs/redis-browser directly.
 */

/** A database connection. */
export type DbConn = {
  id: string
  name: string
  provider: string
  environment: string
  hasConnectionString: boolean
  allowAgentMutations: boolean
  requireApprovalForMutations: boolean
  createdAt: string
  lastConnectedAt: string | null
  details?: DbConnDetails | null
  /** True when the connection name reaches every database on the server. */
  isServerLevel?: boolean
}

export type DbConnDetails = {
  host: string | null
  port: number | null
  database: string | null
  username: string | null
  filePath: string | null
}

/** Query result from the API. */
export type QueryResult = {
  columns: string[]
  rows: unknown[][]
  truncated: boolean
  fromCache?: boolean
  cachedAt?: string | null
  source?: "live" | "offline"
}

/** Schema information from the backend. */
export type SchemaInfo = {
  tables?: TableSchema[]
  databases?: string[]
  fromCache?: boolean
  cachedAt?: string | null
  /** How the answer was obtained: live, cached, or offline when the snapshot answered. */
  source?: "live" | "cached" | "offline"
}

/** One table in a schema. */
export type TableSchema = {
  name: string
  kind: string
  columns?: ColumnSchema[]
  foreignKeys?: {
    column: string
    referencesTable: string
    referencesColumn: string
  }[]
}

/** One column in a table schema. */
export type ColumnSchema = {
  name: string
  dataType: string
  isNullable: boolean
  isPrimaryKey: boolean
}

/** Redis logical database info. Re-exported from redis-browser for convenience. */
export type { RedisDbInfo } from "./redis-browser"

/** One page of the Redis key tree. */
export type { RedisPage } from "./redis-browser"

/** One open tab in the tab bar. */
export type { DbTab } from "./db-tabs"

/** The connection list's sort key and direction. */
export type { ConnSortKey, ConnSortOrder } from "./conn-sorting"

/** How the answer was obtained. */
export type Source = "live" | "offline"

/**
 * The value a Redis key holds, as the inspector reads it.
 */
export type RedisInspect = {
  key: string
  type: string
  ttlSeconds: number
  expiresAt: string | null
  length: number
  encoding: string | null
  /** True when the answer painted from the snapshot; the server has not confirmed it yet. */
  fromCache?: boolean
  cachedAt?: string | null
  preview: {
    kind: string
    entries: Array<{ field: string | null; value: string | null }>
    truncated: boolean
  }
}

import { ConnSortKey, ConnSortOrder } from "./conn-sorting"

/** Notification types. */
export type DbNotification = {
  id: number
  type: "success" | "error" | "warning"
  title: string
  text?: string
}

/** How many toasts are on screen at once; the oldest one goes. */
export const MAX_DB_NOTIFICATIONS = 4

/** The order pair lives in the URL (shared link) and localStorage (reader choice). */
export interface ConnSortState {
  sort: ConnSortKey
  order: ConnSortOrder
}

/** Manual payload shape for the backend. */
export type ManualPayload = {
  host: string | null
  port: number | null
  database: string | null
  username: string | null
  password: string | null
  filePath: string | null
}

/** Provider identifiers. */
export type Provider = "Sqlite" | "MySql" | "Redis"

export type ConnMode = "manual" | "raw"

export const DEFAULT_MYSQL_PORT = 3306

/** A connection detected in an appsettings*.json file, offered for import. */
export type Detected = {
  settingsKey: string
  name: string
  maskedConnectionString: string
  provider: string | null
  rejectionReason: string | null
  sourceFile?: string | null
}