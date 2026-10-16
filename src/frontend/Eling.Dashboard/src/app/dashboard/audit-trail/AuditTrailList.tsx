"use client"

import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import { usePathname, useRouter, useSearchParams } from "next/navigation"
import { RefreshCw } from "lucide-react"

import { DashboardHeader } from "@/components/dashboard-header"
import { Button } from "@/components/ui/button"
import { ButtonGroup } from "@/components/ui/button-group"
import { Input } from "@/components/ui/input"
import { Skeleton } from "@/components/ui/skeleton"
import { useMemoriesSse } from "@/hooks/use-memories-sse"
import { utcToLocal } from "@/lib/date-utils"
import { DateRangePicker } from "@/components/ui/date-range-picker"

type AuditEntry = {
  timestamp: string
  trail: string
  category: string
  action: string
  actor: string
  source: string
  outcome: string
  scope?: string | null
  projectRoot?: string | null
  target?: string | null
  durationMs?: number | null
  query?: string | null
  resultCount?: number | null
  path?: string | null
  byteSize?: number | null
  matches?: number | null
  metadata?: {
    content?: string | null
    tags?: string[] | null
    status?: string | null
    memorySource?: string | null
  } | null
  previousMetadata?: {
    content?: string | null
    tags?: string[] | null
    status?: string | null
    memorySource?: string | null
  } | null
}

type AuditPage = {
  items: AuditEntry[]
  nextCursor: string | null
  from: string
  to: string
  limit: number
}

const TRAILS = [
  { key: "all", label: "All" },
  { key: "changes", label: "Changes" },
  { key: "access", label: "Access" },
] as const

type TrailKey = (typeof TRAILS)[number]["key"]

// Which one-trail endpoints an "all" request fans out to. The engine holds
// both trails in one index but has no cross-trail query, so the page merges
// the two bounded windows itself — newest-first — on one combined cursor.
const ALL_TRAIL_KEYS = ["changes", "access"] as const

function isoDaysAgo(days: number): string {
  return new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString()
}

function isTrailKey(value: string | null): value is TrailKey {
  return value === "all" || value === "changes" || value === "access"
}

/**
 * The audit trail's historical view (design §11). Filters live in the URL
 * (?trail=all&q=...), like the other dashboard list pages, so a reload, a
 * shared link, and the back button all land on the same view. Live streaming
 * and the before/after diff modal are deliberately not here yet — this page
 * reads the same bounded windows the API requires (design §9) and pages by
 * cursor.
 */
export function AuditTrailList() {
  const router = useRouter()
  const pathname = usePathname()
  const searchParams = useSearchParams()

  // Filter state lives in the URL, like the workspaces/codebase pages: absent
  // params mean the default view, which keeps shared URLs clean.
  const trailParam = searchParams.get("trail")
  const trail: TrailKey = isTrailKey(trailParam) ? trailParam : "all"
  const actor = searchParams.get("actor") ?? ""
  const action = searchParams.get("action") ?? ""
  const from = searchParams.get("from") ?? isoDaysAgo(7)
  const to = searchParams.get("to") ?? new Date().toISOString()

  const [items, setItems] = useState<AuditEntry[]>([])
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  // Starts true: the first load is kicked off by the effect below, and every
  // state write in `load` happens after an await (the repo's lint forbids
  // synchronous setState inside an effect body).
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const requestId = useRef(0)

  const setParam = useCallback(
    (key: "trail" | "actor" | "action" | "from" | "to", value: string | null) => {
      const params = new URLSearchParams(searchParams.toString())
      if (value === null || value === "") params.delete(key)
      else params.set(key, value)
      const qs = params.toString()
      router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false })
    },
    [router, pathname, searchParams],
  )

  const queryString = useMemo(() => {
    const params = new URLSearchParams({ from, to, limit: "50" })
    if (actor.trim()) params.set("actor", actor.trim())
    if (action.trim()) params.set("action", action.trim())
    return params.toString()
  }, [from, to, actor, action])

  const load = useCallback(
    async (cursor: string | null) => {
      const id = ++requestId.current
      try {
        // "All" fans out to both one-trail endpoints and merges their
        // windows newest-first on one combined cursor: the cursor is the
        // older of the two trails' cursors, so a click advances whichever
        // trail still has rows. The merge costs two fetches instead of one;
        // acceptable for a bounded-window history page.
        const keys = trail === "all" ? ALL_TRAIL_KEYS : [trail]
        const pages = await Promise.all(
          keys.map(async (key) => {
            const url = `/api/audit/${key}?${queryString}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`
            const res = await fetch(url, { cache: "no-store" })
            if (!res.ok) {
              const text = await res.text()
              throw new Error(text || `Request failed (${res.status})`)
            }
            return (await res.json()) as AuditPage
          }),
        )
        if (id !== requestId.current) return
        const merged = pages
          .flatMap((p) => p.items)
          .sort((a, b) => (a.timestamp < b.timestamp ? 1 : a.timestamp > b.timestamp ? -1 : 0))
        const cursors = pages.map((p) => p.nextCursor).filter((c): c is string => c !== null)
        const combinedCursor = cursors.length > 0 ? cursors.sort().at(0) ?? null : null
        if (cursor) {
          setItems((prev) => [...prev, ...merged])
        } else {
          setItems(merged)
        }
        setNextCursor(combinedCursor)
        setError(null)
      } catch (e) {
        if (id !== requestId.current) return
        setError(e instanceof Error ? e.message : String(e))
      } finally {
        if (id === requestId.current) setLoading(false)
      }
    },
    [trail, queryString],
  )

  useEffect(() => {
    let isMounted = true

    const fetchInitial = async () => {
      if (isMounted) {
        await load(null)
      }
    }

    void fetchInitial()

    return () => {
      isMounted = false
    }
  }, [load])

  // Live view (design §10): reuse the memories SSE stream — mutations and
  // lifecycle changes already broadcast there, so the page refetches its
  // window on those events with no new backend plumbing. Access-only
  // operations broadcast nothing (reads change no state), so the access view
  // stays refresh-driven until a dedicated audit topic exists.
  //
  // Broadcasts arrive in bursts (one memory_save can emit several), so live
  // refetches are debounced: an event schedules a reload 2s out, and a later
  // event inside that window pushes it back. One refetch per burst instead of
  // one per event. The timer lives in a ref; the load callback goes through
  // a ref too, so the SSE subscription below is mounted once and the lint's
  // no-setState-in-effect rule holds.
  const loadRef = useRef(load)
  useEffect(() => {
    loadRef.current = load
  }, [load])
  const liveTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const scheduleLiveReload = useCallback(() => {
    if (liveTimer.current !== null) clearTimeout(liveTimer.current)
    liveTimer.current = setTimeout(() => {
      liveTimer.current = null
      setLoading(true)
      void loadRef.current(null)
    }, 2000)
  }, [])
  useEffect(
    () => () => {
      if (liveTimer.current !== null) clearTimeout(liveTimer.current)
    },
    [],
  )
  const { status: sseStatus } = useMemoriesSse(scheduleLiveReload)

  // Wrapper mirrors the other dashboard list pages: the content column is
  // min-width zero so it never pushes past the sidebar; only the table
  // wrapper scrolls horizontally.
  const contentColumn = "flex min-w-0 w-full flex-1 flex-col gap-4 p-4"

  return (
    <div className="flex w-full flex-col">
      <DashboardHeader label="Audit Trail">
        <span
          className={
            sseStatus === "connected"
              ? "rounded-full bg-emerald-500/15 px-2 py-0.5 text-xs font-medium text-emerald-600 dark:text-emerald-400"
              : "rounded-full bg-muted px-2 py-0.5 text-xs font-medium text-muted-foreground"
          }
          title={
            sseStatus === "connected"
              ? "Live: the list refetches when a mutation is broadcast"
              : sseStatus === "connecting"
                ? "Connecting to the live event stream…"
                : "Live stream unavailable — use Refresh"
          }
        >
          {sseStatus === "connected" ? "● Live" : sseStatus === "connecting" ? "● Connecting" : "○ Offline"}
        </span>
        <Button
          variant="outline"
          size="sm"
          onClick={() => {
            setLoading(true)
            void load(null)
          }}
          disabled={loading}
        >
          <RefreshCw className={loading ? "size-4 animate-spin" : "size-4"} />
          <span className="hidden sm:inline-block">Refresh</span>
        </Button>
      </DashboardHeader>

      <div className={contentColumn}>
        <div className="sticky top-16 z-10 flex min-w-0 flex-wrap items-center gap-2 bg-background py-1">
          <ButtonGroup>
            {TRAILS.map((t) => (
              <Button
                key={t.key}
                size="sm"
                variant={trail === t.key ? "default" : "outline"}
                onClick={() => {
                  setLoading(true)
                  setParam("trail", t.key === "all" ? null : t.key)
                }}
              >
                {t.label}
              </Button>
            ))}
          </ButtonGroup>
          <div className="flex items-center gap-2">
            <DateRangePicker
              from={from}
              to={to}
              onChange={(f, t) => {
                setParam("from", f)
                setParam("to", t)
              }}
              disabled={loading}
            />
          </div>
          <Input
            aria-label="Actor filter"
            className="w-40"
            value={actor}
            onChange={(e) => setParam("actor", e.target.value)}
            placeholder="actor"
          />
          <Input
            aria-label="Action filter"
            className="w-44"
            value={action}
            onChange={(e) => setParam("action", e.target.value)}
            placeholder="action (e.g. memory_save)"
          />
        </div>

        {error && (
          <div className="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
            {error}
          </div>
        )}

        <div className="overflow-x-auto rounded-xl border">
          <table className="w-full min-w-max text-sm">
            <thead>
              <tr className="border-b text-left text-muted-foreground">
                <th className="p-3">Timestamp</th>
                <th className="p-3">Type</th>
                <th className="p-3">Actor</th>
                <th className="p-3">Category</th>
                <th className="p-3">Action</th>
                <th className="p-3">Scope</th>
                <th className="p-3">Target</th>
              </tr>
            </thead>
            <tbody>
              {loading && items.length === 0 && (
                <tr>
                  <td className="p-3" colSpan={7}>
                    <Skeleton className="h-6 w-full" />
                  </td>
                </tr>
              )}
              {!loading && items.length === 0 && !error && (
                <tr>
                  <td className="p-6 text-center text-muted-foreground" colSpan={7}>
                    No entries in this window. Save or search a memory through the
                    MCP tools or the dashboard, then refresh.
                  </td>
                </tr>
              )}
              {items.map((e, i) => (
                <tr className="border-b last:border-0" key={`${e.timestamp}-${e.action}-${i}`}>
                  <td className="p-3 whitespace-nowrap">{utcToLocal(e.timestamp) || e.timestamp}</td>
                  <td className="p-3">
                    <span
                      className={
                        e.trail === "change"
                          ? "rounded-full bg-amber-500/15 px-2 py-0.5 text-xs font-medium text-amber-600 dark:text-amber-400"
                          : "rounded-full bg-sky-500/15 px-2 py-0.5 text-xs font-medium text-sky-600 dark:text-sky-400"
                      }
                    >
                      {e.trail === "change" ? "change" : e.trail === "access" ? "access" : e.trail}
                    </span>
                  </td>
                  <td className="p-3">{e.actor}</td>
                  <td className="p-3">{e.category}</td>
                  <td className="p-3 font-mono text-xs">{e.action}</td>
                  <td className="p-3">{e.scope ?? "—"}</td>
                  <td className="max-w-[240px] truncate p-3 font-mono text-xs">
                    {e.target ?? e.path ?? "—"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        {nextCursor && (
          <div className="flex justify-center">
            <Button
              variant="outline"
              onClick={() => {
                setLoading(true)
                void load(nextCursor)
              }}
              disabled={loading}
            >
              {loading ? "Loading…" : "Load more"}
            </Button>
          </div>
        )}
      </div>
    </div>
  )
}
