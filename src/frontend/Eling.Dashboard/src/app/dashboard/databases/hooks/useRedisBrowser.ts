"use client"

import { useCallback, useEffect, useRef, useState } from "react"
import type { DbConn, DbTab, RedisInspect } from "../types"
import type { RedisDbInfo, RedisPage, RedisPrefixPreview, RedisKeyProbe } from "../redis-browser"
import {
  fetchRedisDatabases,
  fetchRedisKeyDelete,
  fetchRedisKeyProbeBatch,
  fetchRedisPrefixPreview,
  fetchRedisPrefixDelete,
  parseRedisDb,
  pickRedisDb,
  redisPageKey,
  redisPageScope,
} from "../redis-browser"
import { REDIS_TAB_TABLE, isRedisTab, tabKey } from "../db-tabs"
import { API } from "../utils/connection-helpers"
import { apiFetch } from "@/lib/api"
import type { NotifyDb } from "./useNotifications"

/**
 * How long the tree collects keys that came due together before asking.
 *
 * Long enough that a burst - a job finishing, a queue draining - becomes one
 * request; short enough that a row does not sit on "expired" long enough to
 * be noticed.
 */
const PROBE_COALESCE_MS = 250
/**
 * How often an `expired` row is re-probed while it sits in the tree.
 *
 * Carried-over rows have `expiresAt = null`, so they never get "due" by time,
 * and the writer that recreates the key will not re-fetch the page on its
 * own. Without a slow re-check the row would show `expired` forever even
 * though the key came back: it is the one row the reader is staring at.
 */
const PROBE_EXPIRED_RECHECK_MS = 5000
/**
 * The longest a key may wait in that buffer.
 *
 * A plain debounce would reset on every new arrival, so a database whose keys
 * expire in a steady stream would never flush at all. This cap is what turns
 * the window into a coalescer instead of a stall.
 */
const PROBE_MAX_WAIT_MS = 1000

/** How many keys one probe request may carry; the route refuses more. */
const PROBE_BATCH_SIZE = 200

/**
 * How often the tree looks for keys whose countdown has come due.
 *
 * The countdown is drawn from `expiresAt`, so a key reaching zero changes no
 * state at all: something has to look on a clock. A second is fine - the row
 * has been saying "expired" for that long already.
 */
const PROBE_SCAN_INTERVAL_MS = 1000

/**
 * A probe that says "gone" refetches the page it came from, ahead of the
 * AutoRefresh tick. This is the shortest gap between two such refetches: a
 * database whose keys expire in a steady stream must not turn the tree into a
 * refetch loop, and one second is below what a reader can notice anyway.
 */
const GONE_REFETCH_MIN_INTERVAL_MS = 1000

/** One key waiting to be rechecked, with the page it belongs to. */
type RedisProbeBufferEntry = {
  connId: string
  db: number
  pageKey: string
  /** The prefix the page is rooted at, so a "gone" answer can refetch it. */
  prefix: string
  key: string
}

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
  /**
   * The pages as the clock-driven scanner sees them.
   *
   * A key reaching zero changes no state, so the scanner cannot be an effect
   * keyed on `redisPages`: it would only run when a page happened to refetch.
   * It reads this ref instead, on its own interval.
   */
  const redisPagesRef = useRef<Record<string, RedisPage>>({})
  useEffect(() => { redisPagesRef.current = redisPages }, [redisPages])
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
  const [redisAutoRefresh, setRedisAutoRefresh] = useState<boolean>(() => {
    if (typeof window === "undefined") return true
    const v = window.localStorage.getItem("redis.autoRefresh")
    return v === null ? true : v !== "false"
  })
  const [redisAutoRefreshMs, setRedisAutoRefreshMs] = useState<number>(() => {
    if (typeof window === "undefined") return 30000
    const v = Number(window.localStorage.getItem("redis.autoRefreshMs"))
    return v === 10000 || v === 15000 || v === 60000 ? v : 30000
  })
  useEffect(() => {
    try {
      window.localStorage.setItem("redis.autoRefresh", redisAutoRefresh ? "true" : "false")
    } catch {
      // storage unavailable: the toggle still works for this session
    }
  }, [redisAutoRefresh])
  useEffect(() => {
    try {
      window.localStorage.setItem("redis.autoRefreshMs", String(redisAutoRefreshMs))
    } catch {
      // storage unavailable: the interval still works for this session
    }
  }, [redisAutoRefreshMs])
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

  /** The logical databases of a Redis connection.
   *
   * Cache-first like the schema read: the cached paint lands at once and a
   * background revalidate with `refresh: true` asks the server for the real
   * answer, so the list is never stale and never waits for the server. A
   * manual refresh bypasses the cache directly.
   */
  const loadRedisDatabases = useCallback(
    async (connId: string, refresh?: boolean) => {
      try {
        const list = await fetchRedisDatabases(connId, refresh)
        setRedisDbs((prev) => ({ ...prev, [connId]: list }))
        if (!refresh && list.some((d) => d.fromCache)) {
          void fetchRedisDatabases(connId, true).then(
            (live) => setRedisDbs((prev) => ({ ...prev, [connId]: live })),
            () => {},
          )
        }
      } catch (e: unknown) {
        notifyDb("error", "Redis databases failed", e instanceof Error ? e.message : String(e))
      }
    },
    [notifyDb]
  )

  /** Reads one page of keys under a prefix.
   *
   * Cache-first like the schema read: the cached page paints at once and a
   * background revalidate with `refresh: true` asks the server for the real
   * answer. An explicit refresh skips the cache. */
  const fetchRedisPrefix = useCallback(
    async (id: string, database: number, prefix: string, refresh?: boolean, opts?: { silent?: boolean }) => {
      // The spinner belongs to the user who is waiting, not to a background
      // revalidate: a cache paint settles almost instantly, and a user who
      // just opened a tab should not watch Redis' 10s live answer decide
      // whether the page is on screen. Only an explicit refresh or a page we
      // do not have yet is allowed to raise it.
      const pageKey = redisPageKey(id, database, prefix)
      const showLoading = !opts?.silent && (refresh === true || redisPagesRef.current[pageKey] === undefined)
      if (showLoading) redisSetLoading(1)
      try {
        const qs = new URLSearchParams()
        qs.set("database", String(database))
        if (prefix) qs.set("prefix", prefix)
        qs.set("cursor", "0")
        qs.set("pageOffset", "0")
        qs.set("pageSize", "200")
        if (refresh) qs.set("refresh", "true")
        const page = await apiFetch<RedisPage>(`${API}/${id}/keys?${qs.toString()}`)
        setRedisPages((prev) => ({ ...prev, [pageKey]: page }))
        if (!refresh && page.fromCache) {
          void (async () => {
            try {
              const qs2 = new URLSearchParams()
              qs2.set("database", String(database))
              if (prefix) qs2.set("prefix", prefix)
              qs2.set("cursor", "0")
              qs2.set("pageOffset", "0")
              qs2.set("pageSize", "200")
              qs2.set("refresh", "true")
              const live = await apiFetch<RedisPage>(`${API}/${id}/keys?${qs2.toString()}`).catch(() => null)
              if (!live) return
              setRedisPages((prev) => ({ ...prev, [pageKey]: live }))
            } catch {
              // Offline revalidate keeps the cached paint; the next keep-fresh
              // tick or a manual refresh will ask again.
            }
          })()
        }
      } catch (e: unknown) {
        notifyDb("error", "Redis key browse failed", e instanceof Error ? e.message : String(e))
      } finally {
        if (showLoading) redisSetLoading(-1)
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
        const next = await apiFetch<RedisPage>(`${API}/${id}/keys?${qs.toString()}`)
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
          return { ...prev, [key]: { ...next, nodes, keys, cursor, pageOffset, scanned,
            // The merge only keeps the freshest cache stamp the server reported;
            // the client never decides whether a page is cached.
            fromCache: next.fromCache,
            cachedAt: next.cachedAt ?? null,
          } }
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
   * An explicit Browse/Root asks the server, not what we last knew.
   */
  const browseRedisPrefix = useCallback(
    (id: string, database: number, prefix: string) => {
      const target = prefix.trim()
      setRedisPrefix(target)
      setRedisRootPrefix(target)
      setRedisExpanded(new Set())
      void fetchRedisPrefix(id, database, target, true)
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
      void fetchRedisPrefix(id, database, "", true)
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
        const payload = await apiFetch<RedisInspect>(`${API}/${connId}/keys/inspect?${qs.toString()}`)
        // A slower answer must not land on a tab that has moved on.
        if (request !== redisInspectRequestRef.current) return
        setRedisInspect(payload)
        setRedisInspectOpen(true)

        // The inspector just read the key from the server, which is newer and
        // better than whatever the row was saying. A row marked "gone" that a
        // reader clicks and finds alive is the worst case: the list and the
        // panel disagree, and the list is the wrong one.
        const pagePrefix = redisPageScope(connId, activeRedisDb)
        const probeKey = `${connId}:${activeRedisDb}:${key}`
        redisProbedRef.current.delete(probeKey)
        setRedisPages((prev) => {
          let touched = false
          const next = { ...prev }
          for (const [pageKey, page] of Object.entries(prev)) {
            if (!pageKey.startsWith(pagePrefix)) continue
            if (!page.keys.some((leaf) => leaf.key === key)) continue
            touched = true
            next[pageKey] = {
              ...page,
              keys: page.keys.map((leaf) =>
                leaf.key === key
                  ? { ...leaf, gone: false, ttlSeconds: payload.ttlSeconds, expiresAt: payload.expiresAt, expired: false }
                  : leaf,
              ),
            }
          }
          return touched ? next : prev
        })
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
        notifyDb("success", "Redis keys deleted", `${result.deleted} keys under ${prefix}`)
        setRedisPrefixPreviewOpen(false)
      } catch (e: unknown) {
        notifyDb("error", "Redis delete failed", e instanceof Error ? e.message : String(e))
      } finally {
        setRedisMutating(false)
      }
    },
    [redisInspectKey, redisRootPrefix, fetchRedisPrefix, loadRedisDatabases, notifyDb]
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

  // Keep what is on screen current: a Redis tree left open a while should not
  // go stale. The read is cache-first, so it paints from the snapshot and asks
  // again - it never blocks the screen, and the interval is long enough that a
  // slow server cannot stack two of them. The background fetch bypasses the
  // cache so the snapshot is replaced with live data. This is the auto-reload
  // the toggle controls; off means no background fetch.
  useEffect(() => {
    if (!redisAutoRefresh || !isRedisTab(activeTable ?? null) || !activeTable) return
    const connId = activeTable.connId
    const db = activeRedisDb
    const prefix = redisRootPrefix
    const timer = setInterval(() => {
      void fetchRedisPrefix(connId, db, prefix, true, { silent: true })
    }, redisAutoRefreshMs)
    return () => clearInterval(timer)
  }, [redisAutoRefresh, redisAutoRefreshMs, activeTable, activeRedisDb, redisRootPrefix, fetchRedisPrefix])

  /**
   * A key whose countdown reached zero is a claim that has come due: the row
   * says "expired", but a writer may have rewritten the key since the scan, so
   * it may be alive with a fresh expiry. Ask the server, then either take the
   * new expiry or mark the row gone and refetch the page.
   *
   * <para>
   * Keys expire in bunches, so the asking is coalesced rather than debounced.
   * A debounce waits for the input to go quiet, and short-lived keys never do:
   * a key expiring every few hundred milliseconds would keep resetting the
   * timer and no probe would ever run, leaving rows stuck on "expired". This
   * collects for a short window and then flushes, and it flushes anyway once
   * the oldest buffered key has waited <see cref="PROBE_MAX_WAIT_MS"/> - so a
   * steady stream is delayed, never starved. The flush is one request for the
   * whole bunch.
   * </para>
   *
   * <para>
   * A "gone" answer is proof the keyspace moved, so it refreshes the page it
   * came from immediately - ahead of the AutoRefresh tick, which may be a
   * minute away. That refresh replaces the struck-through row with the truth:
   * nothing there if the key really died, or the recreated key with its new
   * TTL. Because the refresh is the recovery, nothing needs to re-probe a gone
   * row afterwards.
   * </para>
   */
  const redisProbedRef = useRef<Set<string>>(new Set())
  const redisProbeBufferRef = useRef<Map<string, RedisProbeBufferEntry>>(new Map())
  const redisProbeTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const redisProbeOldestRef = useRef<number | null>(null)
  /** When each `expired` row may be re-probed next, per key. */
  const redisExpiredRecheckRef = useRef<Map<string, number>>(new Map())
  /** When each page was last refetched because one of its keys went away. */
  const lastGoneRefetchRef = useRef<Map<string, number>>(new Map())

  const flushRedisProbes = useCallback(() => {
    redisProbeTimerRef.current = null
    redisProbeOldestRef.current = null
    const buffered = Array.from(redisProbeBufferRef.current.values())
    redisProbeBufferRef.current.clear()
    if (buffered.length === 0) return

    // The route names one connection and one database, so a buffer that spans
    // both is one request per pair - normally exactly one.
    const groups = new Map<string, { connId: string; db: number; entries: RedisProbeBufferEntry[] }>()
    for (const entry of buffered) {
      const groupKey = `${entry.connId}:${entry.db}`
      const group = groups.get(groupKey)
      if (group) group.entries.push(entry)
      else groups.set(groupKey, { connId: entry.connId, db: entry.db, entries: [entry] })
    }

    for (const { connId, db, entries } of groups.values()) {
      void (async () => {
        // Pages already refetched for this flush: a page whose key vanished is
        // fetched once, not once per vanished key.
        const refetched = new Set<string>()
        for (let i = 0; i < entries.length; i += PROBE_BATCH_SIZE) {
          const chunk = entries.slice(i, i + PROBE_BATCH_SIZE)
          try {
            const answers = await fetchRedisKeyProbeBatch(connId, db, chunk.map((e) => e.key))
            if (answers.length === 0) continue
            const byKey = new Map(answers.map((a) => [a.key, a]))
            // The inspector counts down from the expiry it was given when the
            // row was clicked. If the key was rewritten with a fresh TTL, the
            // answer knows it, so bring the panel in step — otherwise it would
            // sit at "expired" forever until the reader re-clicks.
            setRedisInspect((prev) => {
              if (!prev) return prev
              const answer = byKey.get(prev.key)
              if (!answer) return prev
              return {
                ...prev,
                ttlSeconds: answer.exists ? answer.ttlSeconds : 0,
                expiresAt: answer.expiresAt,
              }
            })

            setRedisPages((prev) => {
              const next = { ...prev }
              // One pass per page, so a page of keys that all expired together
              // is copied once instead of once per key.
              const byPage = new Map<string, Map<string, RedisKeyProbe>>()
              for (const entry of chunk) {
                const answer = byKey.get(entry.key)
                if (!answer) continue
                let forPage = byPage.get(entry.pageKey)
                if (!forPage) {
                  forPage = new Map()
                  byPage.set(entry.pageKey, forPage)
                }
                forPage.set(entry.key, answer)
              }

              for (const [pageKey, answered] of byPage) {
                const page = next[pageKey]
                if (!page) continue
                next[pageKey] = {
                  ...page,
                  keys: page.keys.map((leaf) => {
                    const answer = answered.get(leaf.key)
                    if (!answer) return leaf
                    const probeKey = `${connId}:${db}:${leaf.key}`
                    if (!answer.exists) {
                      redisExpiredRecheckRef.current.delete(probeKey)
                      return { ...leaf, gone: true, ttlSeconds: -1, expiresAt: null, expired: true }
                    }
                    redisExpiredRecheckRef.current.delete(probeKey)
                    return { ...leaf, gone: false, ttlSeconds: answer.ttlSeconds, expiresAt: answer.expiresAt, expired: false }
                  }),
                }
              }
              return next
            })

            // A key that went away proves the page it came from is out of date,
            // so refetch it now instead of waiting for the next AutoRefresh
            // tick, which may be a minute away. One refetch per page even when
            // a dozen keys from it came due together; the fetch is the recovery,
            // so nothing has to re-probe a gone row afterwards. The floor keeps
            // a steady stream of expiring keys from becoming a refetch loop.
            for (const entry of chunk) {
              const answer = byKey.get(entry.key)
              if (!answer || answer.exists) continue
              if (refetched.has(entry.pageKey)) continue
              const last = lastGoneRefetchRef.current.get(entry.pageKey) ?? 0
              if (Date.now() - last < GONE_REFETCH_MIN_INTERVAL_MS) continue
              lastGoneRefetchRef.current.set(entry.pageKey, Date.now())
              refetched.add(entry.pageKey)
              void fetchRedisPrefix(entry.connId, entry.db, entry.prefix, true, { silent: true })
            }
          } catch {
            // A probe that could not be answered leaves the rows as they were;
            // the next keep-fresh tick replaces the whole page anyway.
          }
        }
      })()
    }
  }, [fetchRedisPrefix])

  useEffect(() => () => {
    if (redisProbeTimerRef.current !== null) clearTimeout(redisProbeTimerRef.current)
  }, [])

  /**
   * Finds the keys whose countdown has come due and buffers them.
   *
   * Reads the pages from a ref rather than from the render's closure, because
   * this runs on a clock: a key reaching zero changes nothing in React state
   * (the countdown is drawn from `expiresAt` alone), so an effect keyed on
   * `redisPages` would never fire for it. The tree would sit on "expired"
   * until something unrelated refetched a page - which is exactly the "I have
   * to click a key before it rechecks" bug.
   */
  const scanDueProbes = useCallback(() => {
    const table = activeTableRef.current
    if (!table || !isRedisTab(table)) return
    const connId = table.connId
    const db = table.redisDb ?? 0

    const nowMs = Date.now()
    let added = false
    for (const [pageKey, page] of Object.entries(redisPagesRef.current)) {
      if (!pageKey.startsWith(redisPageScope(connId, db))) continue
      // A response that is not a page must not take the tree down with it.
      if (!page || !Array.isArray(page.keys)) continue
      for (const leaf of page.keys) {
        const probeKey = `${connId}:${db}:${leaf.key}`
        if (leaf.gone) continue
        const expiresMs = leaf.expiresAt ? new Date(leaf.expiresAt).getTime() : Number.NaN
        // A carried-over `expired` row has no expiry to compare against, so
        // it would never look due. Re-check it slowly instead: if the key is
        // rewritten with a fresh TTL the row takes the new expiry and the
        // countdown restarts on its own.
        if (leaf.expired) {
          const next = redisExpiredRecheckRef.current.get(probeKey) ?? 0
          if (nowMs < next) continue
          redisExpiredRecheckRef.current.set(probeKey, nowMs + PROBE_EXPIRED_RECHECK_MS)
        } else {
          // No expiry, or one still in the future: not due. Re-arm it so a
          // later expiry probes again.
          if (!Number.isFinite(expiresMs) || expiresMs > nowMs) {
            redisProbedRef.current.delete(probeKey)
            continue
          }
          if (redisProbedRef.current.has(probeKey)) continue
        }
        if (redisProbeBufferRef.current.has(probeKey)) continue
        redisProbedRef.current.add(probeKey)
        redisProbeBufferRef.current.set(probeKey, { connId, db, pageKey, prefix: page.prefix, key: leaf.key })
        added = true
      }
    }
    if (!added) return

    const now = Date.now()
    if (redisProbeOldestRef.current === null) redisProbeOldestRef.current = now
    const waited = now - redisProbeOldestRef.current

    if (redisProbeTimerRef.current !== null) {
      // A window is already open; let it close on its own unless the oldest
      // buffered key has waited long enough that waiting more would be a stall.
      if (waited < PROBE_MAX_WAIT_MS) return
      clearTimeout(redisProbeTimerRef.current)
      flushRedisProbes()
      return
    }

    redisProbeTimerRef.current = setTimeout(
      flushRedisProbes,
      Math.max(0, Math.min(PROBE_COALESCE_MS, PROBE_MAX_WAIT_MS - waited)),
    )
  }, [flushRedisProbes])

  // One tick a second is enough to notice a countdown that reached zero, and
  // cheap: nothing is fetched unless a key is actually due.
  useEffect(() => {
    const timer = setInterval(scanDueProbes, PROBE_SCAN_INTERVAL_MS)
    return () => clearInterval(timer)
  }, [scanDueProbes])

  // Run it as soon as a page arrives too, so the first expired key does not
  // wait a whole tick.
  useEffect(() => { scanDueProbes() }, [redisPages, scanDueProbes])

  useEffect(() => () => {
    if (redisProbeTimerRef.current !== null) clearTimeout(redisProbeTimerRef.current)
  }, [])

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
    redisAutoRefresh,
    setRedisAutoRefresh,
    redisAutoRefreshMs,
    setRedisAutoRefreshMs,
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
