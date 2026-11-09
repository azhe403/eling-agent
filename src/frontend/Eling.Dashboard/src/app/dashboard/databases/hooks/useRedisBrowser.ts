"use client"

import { useCallback, useEffect, useRef, useState } from "react"
import type { DbConn, DbTab, RedisInspect } from "../types"
import type { RedisDbInfo, RedisPage, RedisPrefixPreview } from "../redis-browser"
import {
  fetchRedisDatabases,
  fetchRedisKeyDelete,
  fetchRedisPrefixPreview,
  fetchRedisPrefixDelete,
  parseRedisDb,
  pickRedisDb,
  redisPageKey,
  redisPageScope,
} from "../redis-browser"
import { REDIS_TAB_TABLE, isRedisTab, tabKey } from "../db-tabs"
import { API, parseApiError } from "../utils/connection-helpers"
import type { NotifyDb } from "./useNotifications"

/**
 * The redis side of the explorer: the databases of a connection, the pages
 * under each prefix, and the key shown on the right.
 *
 * The tree is read lazily, one prefix at a time: a busy database holds far
 * more keys than a screen can show, so the scan walks down as the reader
 * opens it.
 */
export function useRedisBrowser({
  notifyDb,
  activeTable,
  setActiveTable,
  activeTableRef,
  setSelectedId,
  setSchemaConnId,
  setOpenTabs,
  setRightTab,
  expandedConns,
  setExpandedConns,
}: {
  notifyDb: NotifyDb
  activeTable: DbTab | null
  setActiveTable: React.Dispatch<React.SetStateAction<DbTab | null>>
  activeTableRef: React.MutableRefObject<DbTab | null>
  setSelectedId: React.Dispatch<React.SetStateAction<string | null>>
  setSchemaConnId: React.Dispatch<React.SetStateAction<string | null>>
  setOpenTabs: React.Dispatch<React.SetStateAction<DbTab[]>>
  setRightTab: React.Dispatch<React.SetStateAction<"data" | "schema" | "sql">>
  expandedConns: Set<string>
  setExpandedConns: React.Dispatch<React.SetStateAction<Set<string>>>
  conns: DbConn[]
}) {
  const [redisDbs, setRedisDbs] = useState<Record<string, RedisDbInfo[]>>({})
  const [redisPages, setRedisPages] = useState<Record<string, RedisPage>>({})
  const [redisExpanded, setRedisExpanded] = useState<Set<string>>(new Set())
  const [redisInspectKey, setRedisInspectKey] = useState("")
  const [redisLoading, setRedisLoading] = useState(false)
  const [redisInspect, setRedisInspect] = useState<RedisInspect | null>(null)
  const [redisInspectOpen, setRedisInspectOpen] = useState(false)
  const [redisPrefix, setRedisPrefix] = useState("")
  /**
   * The prefix the tree is rooted at. Empty means the whole database; anything
   * else means "show only the keys under this prefix". Kept apart from
   * `redisPrefix` (what the box currently says) so typing does not re-root the
   * tree until the reader asks for it.
   */
  const [redisRootPrefix, setRedisRootPrefix] = useState("")
  /**
   * The delete confirmation the dialog names: the key or the prefix the trash
   * button asked for, and whether that dialog is open. The open flags are
   * separate from the targets so a cancel can close without losing where the
   * dialog would reopen.
   */
  const [redisDeleteTarget, setRedisDeleteTarget] = useState<string | null>(null)
  const [redisDeleteOpen, setRedisDeleteOpen] = useState(false)
  const [redisPrefixPreviewTarget, setRedisPrefixPreviewTarget] = useState<string | null>(null)
  const [redisPrefixPreviewOpen, setRedisPrefixPreviewOpen] = useState(false)
  /** The preview a subtree delete confirms against; null until one loads. */
  const [redisPreview, setRedisPreview] = useState<RedisPrefixPreview | null>(null)
  const [redisPreviewLoading, setRedisPreviewLoading] = useState(false)
  const [redisMutating, setRedisMutating] = useState(false)
  const redisInFlightRef = useRef(0)
  /** Guards the inspector against an answer for a key that is no longer shown. */
  const redisInspectRequestRef = useRef(0)

  /** The logical database the open tab shows. */
  const activeRedisDb = isRedisTab(activeTable ?? null) ? (activeTable?.redisDb ?? 0) : 0

  /**
   * A counter, not a flag: two levels opened at once keep the spinner alive
   * until both land, and a late answer for a level that is no longer shown
   * cannot lift a flag the other one is still holding.
   */
  const redisSetLoading = (delta: number) => {
    redisInFlightRef.current = Math.max(0, redisInFlightRef.current + delta)
    setRedisLoading(redisInFlightRef.current > 0)
  }

  /** The logical databases of a Redis connection. */
  const loadRedisDatabases = useCallback(
    async (connId: string) => {
      try {
        const list = await fetchRedisDatabases(connId)
        setRedisDbs((prev) => ({ ...prev, [connId]: list }))
      } catch (e: unknown) {
        notifyDb("error", "Redis databases failed", e instanceof Error ? e.message : String(e))
      }
    },
    [notifyDb]
  )

  /** Reads one page of keys under a prefix. */
  const fetchRedisPrefix = useCallback(
    async (id: string, database: number, prefix: string) => {
      redisSetLoading(1)
      try {
        const qs = new URLSearchParams()
        qs.set("database", String(database))
        if (prefix) qs.set("prefix", prefix)
        qs.set("cursor", "0")
        qs.set("pageOffset", "0")
        qs.set("pageSize", "200")
        const res = await fetch(`${API}/${id}/keys?${qs.toString()}`)
        if (!res.ok) throw new Error(parseApiError(await res.text()))
        const page = (await res.json()) as RedisPage
        setRedisPages((prev) => ({ ...prev, [redisPageKey(id, database, prefix)]: page }))
      } catch (e: unknown) {
        notifyDb("error", "Redis key browse failed", e instanceof Error ? e.message : String(e))
      } finally {
        redisSetLoading(-1)
      }
    },
    [notifyDb]
  )

  /**
   * Continue a page that stopped before the server's scan did: the cursor and
   * page offset the previous page left behind are sent back, so the next
   * answer resumes where the last one stopped instead of restarting it.
   */
  const loadMoreRedis = useCallback(
    async (id: string, database: number, prefix: string) => {
      const key = redisPageKey(id, database, prefix)
      const page = redisPages[key]
      if (!page || page.complete) return
      redisSetLoading(1)
      try {
        const qs = new URLSearchParams()
        qs.set("database", String(database))
        if (prefix) qs.set("prefix", prefix)
        qs.set("cursor", String(page.cursor))
        qs.set("pageOffset", String(page.pageOffset))
        qs.set("pageSize", "200")
        const res = await fetch(`${API}/${id}/keys?${qs.toString()}`)
        if (!res.ok) throw new Error(parseApiError(await res.text()))
        const next = (await res.json()) as RedisPage
        // The continuation appends to what the page already has: a node or key
        // the two answers both carry is one thing, not two.
        setRedisPages((prev) => {
          const base = prev[key]
          if (!base) return { ...prev, [key]: next }
          const seenNodes = new Set(base.nodes.map((n) => n.prefix))
          const seenKeys = new Set(base.keys.map((k) => k.key))
          const nodes = [...base.nodes, ...next.nodes.filter((n) => !seenNodes.has(n.prefix))]
          const keys = [...base.keys, ...next.keys.filter((k) => !seenKeys.has(k.key))]
          const cursor = next.complete ? 0 : next.cursor
          const pageOffset = next.complete ? 0 : next.pageOffset
          // `scanned` counts what one answer scanned, so the running total is the sum.
          const scanned = base.scanned + next.scanned
          return { ...prev, [key]: { ...next, nodes, keys, cursor, pageOffset, scanned } }
        })
      } catch (e: unknown) {
        notifyDb("error", "Redis key browse failed", e instanceof Error ? e.message : String(e))
      } finally {
        redisSetLoading(-1)
      }
    },
    [redisPages, notifyDb]
  )

  /**
   * Roots the tree at the prefix in the box. The scan is prefix-filtered on
   * the server, so a narrow prefix is also the cheap way to read a busy database.
   */
  const browseRedisPrefix = useCallback(
    (id: string, database: number, prefix: string) => {
      const target = prefix.trim()
      setRedisPrefix(target)
      setRedisRootPrefix(target)
      setRedisExpanded(new Set())
      void fetchRedisPrefix(id, database, target)
    },
    [fetchRedisPrefix]
  )

  /** Drops every page of one database and reads its root again. */
  const rootRedisPrefix = useCallback(
    (id: string, database: number) => {
      setRedisPrefix("")
      setRedisRootPrefix("")
      const scope = redisPageScope(id, database)
      setRedisPages((prev) => {
        const next = { ...prev }
        for (const k of Object.keys(next)) {
          if (k.startsWith(scope)) delete next[k]
        }
        return next
      })
      setRedisExpanded((prev) => new Set([...prev].filter((k) => !k.startsWith(scope))))
      void fetchRedisPrefix(id, database, "")
    },
    [fetchRedisPrefix]
  )

  /** Closes every prefix of one database, keeping the pages that were read. */
  const collapseRedis = useCallback((id: string, database: number) => {
    const scope = redisPageScope(id, database)
    setRedisExpanded((prev) => new Set([...prev].filter((k) => !k.startsWith(scope))))
  }, [])

  /** Opens or closes one prefix, reading it the first time. */
  const toggleRedisPrefix = useCallback(
    async (id: string, prefix: string) => {
      const key = redisPageKey(id, activeRedisDb, prefix)
      const isExpanded = redisExpanded.has(key)
      setRedisExpanded((prev) => {
        const next = new Set(prev)
        if (isExpanded) next.delete(key)
        else next.add(key)
        return next
      })
      if (!isExpanded && !redisPages[key]) {
        await fetchRedisPrefix(id, activeRedisDb, prefix)
      }
    },
    [activeRedisDb, redisExpanded, redisPages, fetchRedisPrefix]
  )

  /** Reads the value of a key for the inspector. */
  const selectRedisKey = useCallback(
    async (key: string) => {
      // The key belongs to the connection of the tab that shows it, not to
      // whatever the sidebar happens to have selected: picking another
      // connection while a Redis tab is open used to ask the wrong server.
      const connId = activeTable?.connId
      if (!connId) return
      const request = ++redisInspectRequestRef.current
      setRedisInspectKey(key)
      try {
        const qs = new URLSearchParams()
        qs.set("key", key)
        qs.set("database", String(activeRedisDb))
        const res = await fetch(`${API}/${connId}/keys/inspect?${qs.toString()}`)
        if (!res.ok) throw new Error(parseApiError(await res.text()))
        const payload = (await res.json()) as RedisInspect
        // A slower answer must not land on a tab that has moved on.
        if (request !== redisInspectRequestRef.current) return
        setRedisInspect(payload)
        setRedisInspectOpen(true)
      } catch (e: unknown) {
        if (request !== redisInspectRequestRef.current) return
        notifyDb("error", "Redis inspect failed", e instanceof Error ? e.message : String(e))
        setRedisInspect(null)
        setRedisInspectKey("")
      }
    },
    [activeTable, activeRedisDb, notifyDb]
  )

  /**
   * Delete one key from the dashboard. No optimistic removal: the tree keeps
   * showing the key until the server confirms, and a failure keeps the
   * dialog open so the reader can cancel or retry.
   *
   * On success the inspector is cleared when it names the deleted key, the
   * cached pages of this database drop the key, and the root page plus the
   * database list are reloaded so the count matches.
   */
  const deleteRedisKey = useCallback(
    async (connId: string, db: number, key: string) => {
      setRedisMutating(true)
      try {
        await fetchRedisKeyDelete(connId, db, key)
        if (redisInspectKey === key) {
          setRedisInspect(null)
          setRedisInspectKey("")
          setRedisInspectOpen(false)
        }
        const scope = redisPageScope(connId, db)
        setRedisPages((prev) => {
          const next = { ...prev }
          for (const pageKey of Object.keys(next)) {
            if (!pageKey.startsWith(scope)) continue
            const page = next[pageKey]
            next[pageKey] = { ...page, keys: page.keys.filter((item) => item.key !== key) }
          }
          return next
        })
        void fetchRedisPrefix(connId, db, redisRootPrefix)
        void loadRedisDatabases(connId)
        notifyDb("success", "Redis key deleted", key)
        setRedisDeleteOpen(false)
      } catch (e: unknown) {
        notifyDb("error", "Redis delete failed", e instanceof Error ? e.message : String(e))
      } finally {
        setRedisMutating(false)
      }
    },
    [redisInspectKey, redisRootPrefix, fetchRedisPrefix, loadRedisDatabases, notifyDb]
  )

  /**
   * Read the affected keys of one subtree before the delete dialog confirms,
   * so the reader sees what will go away. The dialog opens as the preview
   * loads; a failed preview closes it again rather than showing an empty list.
   */
  const previewRedisPrefix = useCallback(
    async (connId: string, db: number, prefix: string) => {
      setRedisPrefixPreviewTarget(prefix)
      setRedisPreview(null)
      setRedisPreviewLoading(true)
      setRedisPrefixPreviewOpen(true)
      try {
        const preview = await fetchRedisPrefixPreview(connId, db, prefix)
        setRedisPreview(preview)
      } catch (e: unknown) {
        notifyDb("error", "Redis preview failed", e instanceof Error ? e.message : String(e))
        setRedisPrefixPreviewOpen(false)
      } finally {
        setRedisPreviewLoading(false)
      }
    },
    [notifyDb]
  )

  /**
   * Delete every key under one subtree, after the preview named them. Same
   * pattern as the single key: the inspector is cleared when it names a key
   * inside the subtree, pages of this database inside the subtree are
   * dropped, and the root plus the database list reload. A failure keeps the
   * dialog open.
   */
  const deleteRedisPrefix = useCallback(
    async (connId: string, db: number, prefix: string) => {
      setRedisMutating(true)
      try {
        const result = await fetchRedisPrefixDelete(connId, db, prefix)
        if (redisInspectKey !== "" && redisInspectKey.startsWith(prefix)) {
          setRedisInspect(null)
          setRedisInspectKey("")
          setRedisInspectOpen(false)
        }
        const scope = redisPageScope(connId, db)
        setRedisPages((prev) => {
          const next: Record<string, RedisPage> = {}
          for (const [pageKey, page] of Object.entries(prev)) {
            if (!pageKey.startsWith(scope)) {
              next[pageKey] = page
              continue
            }
            if (page.prefix === prefix || page.prefix.startsWith(prefix)) continue
            next[pageKey] = {
              ...page,
              nodes: page.nodes.filter((n) => !(n.prefix === prefix || n.prefix.startsWith(prefix))),
              keys: page.keys.filter((k) => !k.key.startsWith(prefix)),
            }
          }
          return next
        })
        setRedisExpanded((prev) => {
          const next = new Set<string>()
          for (const entry of prev) {
            if (!entry.startsWith(scope)) {
              next.add(entry)
              continue
            }
            const suffix = entry.slice(scope.length)
            if (suffix === prefix || suffix.startsWith(prefix)) continue
            next.add(entry)
          }
          return next
        })
        void fetchRedisPrefix(connId, db, redisRootPrefix)
        void loadRedisDatabases(connId)
        const total = redisPreview?.prefix === prefix ? redisPreview.total : result.deleted
        notifyDb("success", "Redis keys deleted", `${total} keys under ${prefix}`)
        setRedisPrefixPreviewOpen(false)
      } catch (e: unknown) {
        notifyDb("error", "Redis delete failed", e instanceof Error ? e.message : String(e))
      } finally {
        setRedisMutating(false)
      }
    },
    [redisInspectKey, redisPreview, redisRootPrefix, fetchRedisPrefix, loadRedisDatabases, notifyDb]
  )

  /** Opens or closes a Redis connection in the tree. */
  const toggleRedisConn = useCallback(
    (id: string) => {
      const wasExpanded = expandedConns.has(id)
      setExpandedConns((prev) => {
        const next = new Set(prev)
        if (wasExpanded) next.delete(id)
        else next.add(id)
        return next
      })
      if (!wasExpanded) void loadRedisDatabases(id)
    },
    [expandedConns, loadRedisDatabases, setExpandedConns]
  )

  /** Opens one logical Redis database in a tab of its own. */
  const openRedisDb = useCallback(
    (connId: string, dbIndex: number) => {
      const tab: DbTab = { connId, table: REDIS_TAB_TABLE, database: null, kind: "redis", redisDb: dbIndex }
      setOpenTabs((prev) => (prev.some((t) => tabKey(t) === tabKey(tab)) ? prev : [...prev, tab]))
      setActiveTable(tab)
      activeTableRef.current = tab
      setSelectedId(connId)
      setSchemaConnId(connId)
      setRightTab("data")
      setRedisInspect(null)
      setRedisInspectKey("")
      const rootKey = redisPageKey(connId, dbIndex, "")
      if (!redisPages[rootKey]) void fetchRedisPrefix(connId, dbIndex, "")
    },
    [redisPages, fetchRedisPrefix, setActiveTable, setOpenTabs, setSelectedId, setSchemaConnId, setRightTab]
  )

  // The inspector belongs to one key of one database of one connection: reset
  // it when any of those change, so a stale value is not shown beside another
  // tree. Keyed on the database alone it survived switching connection.
  // The root prefix goes with them: a filter typed for one database means
  // nothing in another.
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- the inspected key belongs to another connection; drop it.
    setRedisInspect(null)
    setRedisInspectKey("")
    setRedisRootPrefix("")
    setRedisPrefix("")
  }, [activeTable?.connId, activeRedisDb])

  return {
    redisDbs,
    setRedisDbs,
    redisPages,
    setRedisPages,
    redisExpanded,
    setRedisExpanded,
    redisInspectKey,
    setRedisInspectKey,
    redisLoading,
    setRedisLoading,
    redisInspect,
    setRedisInspect,
    redisInspectOpen,
    setRedisInspectOpen,
    redisPrefix,
    setRedisPrefix,
    redisRootPrefix,
    setRedisRootPrefix,
    redisDeleteTarget,
    setRedisDeleteTarget,
    redisDeleteOpen,
    setRedisDeleteOpen,
    redisPrefixPreviewTarget,
    redisPrefixPreviewOpen,
    setRedisPrefixPreviewOpen,
    redisPreview,
    setRedisPreview,
    redisPreviewLoading,
    redisMutating,
    activeRedisDb,
    redisSetLoading,
    loadRedisDatabases,
    fetchRedisPrefix,
    loadMoreRedis,
    browseRedisPrefix,
    rootRedisPrefix,
    collapseRedis,
    toggleRedisPrefix,
    selectRedisKey,
    toggleRedisConn,
    openRedisDb,
    deleteRedisKey,
    previewRedisPrefix,
    deleteRedisPrefix,
  }
}

/** The redis helper that picks the database a URL or a saved tab names. */
export const pickRestoredRedisDb = (
  urlDb: string | null,
  savedTab: { connId: string; redisDb?: number } | null,
  connId: string,
  available: RedisDbInfo[]
): number => pickRedisDb({ urlDb, savedTab, connId, available })

/** The redis helper that reads the database index an URL names. */
export const parseRestoredRedisDb = parseRedisDb
