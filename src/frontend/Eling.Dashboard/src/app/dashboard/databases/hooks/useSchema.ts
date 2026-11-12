"use client"

import { useCallback, useEffect, useRef, useState } from "react"
import { SchemaInfo } from "../types"
import { useExponentialRetry } from "../useExponentialRetry"
import { API } from "../utils/connection-helpers"
import { apiFetch, ApiError } from "@/lib/api"
import { NotifyDb } from "./useNotifications"

/** How often a tree that is on screen is read again. */
const KEEP_FRESH_MS = 30000

/** Reads the schema of a connection, or of one database on a server-level one. */
const fetchSchema = (
  id: string,
  database?: string | null,
  refresh?: boolean
): Promise<SchemaInfo> => {
  const params = new URLSearchParams()
  if (database) params.set("database", database)
  if (refresh) params.set("refresh", "true")
  const qs = params.toString() ? "?" + params.toString() : ""
  return apiFetch<SchemaInfo>(API + "/" + id + "/schema" + qs)
}

/**
 * The schemas of the connections and databases on screen.
 *
 * A read is cache-first: the snapshot paints at once and the real answer is
 * asked for behind it. A database that cannot be reached is asked again on a
 * doubling ladder, with the countdown shown next to the tree.
 *
 * `isSqlConnection` says whether a connection is one this read has anything to
 * say about. A Redis connection has no tables and no columns, so /schema
 * refuses it — and every caller here would otherwise ask anyway, once per
 * interval, per expanded connection, and get a 400 back each time. The guard
 * lives in this hook rather than at the call sites: there are six of them and
 * a seventh would forget.
 */
export function useSchema(
  notifyDb: NotifyDb,
  isSqlConnection: (connId: string) => boolean = () => true,
  /** Called with the retry-ladder cache key when /schema answers 404: the registry no longer has the connection. */
  onConnectionMissing?: (key: string) => void,
) {
  const [schemaInfo, setSchemaInfo] = useState<SchemaInfo | null>(null)
  const [connSchemas, setConnSchemas] = useState<Record<string, SchemaInfo>>({})
  const [dbSchemas, setDbSchemas] = useState<Record<string, SchemaInfo>>({})
  const [schemaLoading, setSchemaLoading] = useState(false)

  const connSchemasRef = useRef<Record<string, SchemaInfo>>({})
  const dbSchemasRef = useRef<Record<string, SchemaInfo>>({})
  const schemaConnIdRef = useRef<string | null>(null)
  /** The newest predicate, so a closure from an earlier render cannot go stale. */
  const isSqlConnectionRef = useRef(isSqlConnection)
  /** Same for the missing-connection callback. */
  const onConnectionMissingRef = useRef(onConnectionMissing)

  useEffect(() => { connSchemasRef.current = connSchemas }, [connSchemas])
  useEffect(() => { dbSchemasRef.current = dbSchemas }, [dbSchemas])
  useEffect(() => { isSqlConnectionRef.current = isSqlConnection }, [isSqlConnection])
  useEffect(() => { onConnectionMissingRef.current = onConnectionMissing }, [onConnectionMissing])

  const {
    schedule: scheduleRetry,
    cancel: cancelRetry,
    attemptOf: retryAttemptOf,
    label: retryLabel,
  } = useExponentialRetry()

  /**
   * Tell the page the connection is gone, so the tree can prune it and its
   * retry ladder can stop. The key is the same cache key the retry ladder
   * uses: the connection id for a top-level read, or `connId:database` for
   * one. Only a 404 ends the ladder - a database that is merely offline is
   * retried on the same schedule its neighbours are.
   */
  const reportConnectionMissing = useCallback((key: string, e: unknown) => {
    if (!(e instanceof ApiError) || e.status !== 404) return
    onConnectionMissingRef.current?.(key)
    cancelRetry(key)
  }, [cancelRetry])

  /**
   * The reads reach themselves through a ref: the retry ladder calls them
   * again, and a closure that named itself directly would hold the first
   * render's version forever.
   */
  const ensureSchemaRef = useRef<(id: string, opts?: { force?: boolean; bypass?: boolean }) => Promise<void>>(
    async () => {}
  )
  const ensureDbSchemaRef = useRef<(connId: string, database: string, opts?: { force?: boolean; bypass?: boolean }) => Promise<void>>(async () => {})

  /**
   * Reads the schema of a connection, unless a live answer is already here.
   *
   * The callers below are useCallback closures, so keeping the bookkeeping in
   * local state would have them read the first render's values forever.
   */
  const ensureSchema = useCallback(
    async (id: string, opts?: { force?: boolean; bypass?: boolean }) => {
      // Not a SQL connection: /schema would refuse it, so do not ask.
      if (!isSqlConnectionRef.current(id)) return
      const force = opts?.force ?? false
      // A manual refresh asks the database; a retry asks for the best answer the
      // backend can give, which is the snapshot when it is still current. Only
      // the first one skips the cache.
      const bypass = opts?.bypass ?? false
      if (!force && connSchemasRef.current[id]?.source === "live") return

      try {
        const fresh = await fetchSchema(id, null, bypass)
        setConnSchemas((prev) => ({ ...prev, [id]: fresh }))
        if (schemaConnIdRef.current === id) setSchemaInfo(fresh)
        // A cache answer paints at once and asks again for the real answer;
        // offline keeps the countdown so a dead database is not probed
        // repeatedly. Both ask for the real answer, so both skip the cache.
        if (fresh.source === "cached" || fresh.source === "offline") {
          scheduleRetry(id, fresh.source === "offline" ? "offline" : "revalidate", () => {
            void ensureSchemaRef.current(id, { force: true, bypass: true })
          })
        } else {
          cancelRetry(id)
        }
      } catch (e: unknown) {
        // The database could not be reached at all: there is no cached answer
        // either, so the retry ladder runs on the same schedule. The error is
        // reported once - ten toasts for one dead database is noise.
        const first = retryAttemptOf(id) === 0
        scheduleRetry(id, "offline", () => { void ensureSchemaRef.current(id, { force: true, bypass: true }) })
        reportConnectionMissing(id, e)
        if (first) notifyDb("error", e instanceof Error ? e.message : "schema failed")
      }
    },
    [notifyDb, scheduleRetry, cancelRetry, retryAttemptOf, reportConnectionMissing]
  )

  /**
   * The tables of one database on a server-level connection. The database
   * name is part of the cache key, so two databases never share a schema.
   */
  const ensureDbSchema = useCallback(
    async (connId: string, database: string, opts?: { force?: boolean; bypass?: boolean }) => {
      // Same refusal as above, one level down.
      if (!isSqlConnectionRef.current(connId)) return
      const key = `${connId}:${database}`
      // Same skip as the connection-level read: a live answer sticks.
      const force = opts?.force ?? false
      const bypass = opts?.bypass ?? false
      if (!force && dbSchemasRef.current[key]?.source === "live") return

      try {
        const fresh = await fetchSchema(connId, database, bypass)
        setDbSchemas((prev) => ({ ...prev, [key]: fresh }))
        if (fresh.source === "cached" || fresh.source === "offline") {
          scheduleRetry(key, fresh.source === "offline" ? "offline" : "revalidate", () => {
            void ensureDbSchemaRef.current(connId, database, { bypass: true })
          })
        } else {
          cancelRetry(key)
        }
      } catch (e: unknown) {
        // Unreachable: the retry ladder runs on the same schedule, and the tree
        // shows the same countdown under this database.
        const first = retryAttemptOf(key) === 0
        scheduleRetry(key, "offline", () => { void ensureDbSchemaRef.current(connId, database, { bypass: true }) })
        reportConnectionMissing(key, e)
        if (first) notifyDb("error", e instanceof Error ? e.message : "schema failed")
      }
    },
    [notifyDb, scheduleRetry, cancelRetry, retryAttemptOf, reportConnectionMissing]
  )

  useEffect(() => { ensureSchemaRef.current = ensureSchema }, [ensureSchema])
  useEffect(() => { ensureDbSchemaRef.current = ensureDbSchema }, [ensureDbSchema])

  /**
   * An explicit refresh from the tree. It bypasses the cache: the reader asked
   * for the database, not for what we last knew.
   */
  const loadSchema = useCallback(
    async (id: string, database?: string | null) => {
      // A manual "Refresh schema" on a Redis connection is a no-op, not a 400.
      if (!isSqlConnectionRef.current(id)) return
      schemaConnIdRef.current = id
      setSchemaLoading(true)
      setSchemaInfo(null)
      try {
        const data = await fetchSchema(id, database, true)
        setSchemaInfo(data)
        setConnSchemas((prev) => ({ ...prev, [id]: data }))
        if (database) setDbSchemas((prev) => ({ ...prev, [`${id}:${database}`]: data }))
      } catch (e: unknown) {
        reportConnectionMissing(database ? `${id}:${database}` : id, e)
        notifyDb("error", e instanceof Error ? e.message : "schema failed")
      } finally {
        setSchemaLoading(false)
      }
    },
    [notifyDb, reportConnectionMissing]
  )

  return {
    schemaInfo,
    setSchemaInfo,
    connSchemas,
    setConnSchemas,
    dbSchemas,
    setDbSchemas,
    schemaLoading,
    connSchemasRef,
    dbSchemasRef,
    schemaConnIdRef,
    ensureSchema,
    ensureDbSchema,
    loadSchema,
    retryLabel,
    KEEP_FRESH_MS,
  }
}
