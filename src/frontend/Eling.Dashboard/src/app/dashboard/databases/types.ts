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
  cacheTtlSeconds?: number
}

/** Schema information from the backend. */
export type SchemaInfo = {
  tables?: TableSchema[]
  databases?: string[]
  fromCache?: boolean
  cachedAt?: string | null
  /** How the answer was obtained: live, cached, or offline when the snapshot answered. */
  source?: "live" | "cached" | "offline"
  /** TTL in seconds for the cached schema (typically 7 days). */
  cacheTtlSeconds?: number
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
  indexes?: {
    name: string
    isUnique: boolean
    columns: string[]
  }[]
}

/** One column in a table schema. */
export type ColumnSchema = {
  name: string
  dataType: string
  isNullable: boolean
  isPrimaryKey: boolean
  /** Declared length of a char/varchar/binary column; absent for other types. */
  maxLength?: number | null
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
export type Provider = "Sqlite" | "MySql" | "Redis" | "PostgreSql" | "Elasticsearch"

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

/* --------------------------------------------------------- Elasticsearch */

/**
 * Elasticsearch: the dashboard reads the cluster's indices. These are the
 * wire shapes of the /api/databases/{id}/es/... routes; the backend
 * serializes them camelCase.
 */

/** One index of the cluster, as the indices route reports it. */
export type ElasticsearchIndexInfo = {
  name: string
  health: string
  status: string
  docsCount: number
  storeSizeBytes: number
  primaryShards: number
  replicas: number
}

/** The mappings and settings of one index, verbatim from the cluster. */
export type ElasticsearchIndexDetail = {
  name: string
  /** The raw mappings payload. */
  mappings: unknown
  /** The raw settings payload. */
  settings: unknown
  docsCount: number
  storeSizeBytes: number
}

/** One hit of a documents or search page. */
export type ElasticsearchHit = {
  index: string
  id: string
  /** Null when the hit has no score. */
  score: number | null
  /** The _source of the hit, verbatim. */
  source: unknown
}

/** The page a documents fetch returns. */
export type ElasticsearchDocumentPage = {
  total: number
  from: number
  size: number
  tookMs: number
  hits: ElasticsearchHit[]
}

/** One bucket of the histogram the endpoint answers. */
export type ElasticsearchHistogramBucket = {
  keyMs: number
  docCount: number
}

/** The histogram answer: the field it histograms, the width, and the buckets. */
export type ElasticsearchHistogram = {
  field: string
  /** The width the cluster chose, when it reports one. */
  interval: string | null
  buckets: ElasticsearchHistogramBucket[]
}

/** One search page of the DSL console: totals, hits and aggregations verbatim. */
export type ElasticsearchSearchResult = {
  total: number
  tookMs: number
  hits: ElasticsearchHit[]
  aggregations: unknown
}

/** The answer to a count: how many documents the query matched. */
export type ElasticsearchCountResult = {
  count: number
  tookMs: number
}

/** The options of the documents fetch. */
export type EsDocumentsOpts = {
  from: number
  size: number
  q?: string | null
  start?: string | null
  end?: string | null
  field?: string | null
  filters?: string | null
  logic?: string | null
}

/** The options of the histogram fetch: the same filters, plus the bucket count. */
export type EsHistogramOpts = {
  buckets?: number
  q?: string | null
  start?: string | null
  end?: string | null
  field?: string | null
  filters?: string | null
  logic?: string | null
}

/** The body of the search fetch; the index travels as the second argument. */
export type EsSearchBody = {
  query?: unknown
  sort?: unknown
  aggs?: unknown
  size?: number
  from?: number
  sourceIncludes?: string[] | null
}

/** Elasticsearch index being inspected in split-view (like RedisInspect). */
export type EsInspect = {
  connId: string
  index: string
  /** True when the answer painted from the snapshot; the server has not confirmed it yet. */
  fromCache?: boolean
  cachedAt?: string | null
}