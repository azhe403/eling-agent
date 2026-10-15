"use client"

import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import { RefreshCw } from "lucide-react"

import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Skeleton } from "@/components/ui/skeleton"

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

const LAST_AUDIT_VIEW_KEY = "eling-audit-trail-view"

function loadLastView(): { trail?: string; actor?: string; action?: string } {
  try {
    const raw = localStorage.getItem(LAST_AUDIT_VIEW_KEY)
    if (!raw) return {}
    const parsed = JSON.parse(raw) as { trail?: unknown; actor?: unknown; action?: unknown }
    return {
      trail: typeof parsed.trail === "string" ? parsed.trail : undefined,
      actor: typeof parsed.actor === "string" ? parsed.actor : undefined,
      action: typeof parsed.action === "string" ? parsed.action : undefined,
    }
  } catch {
    return {}
  }
}

function saveLastView(view: { trail: TrailKey; actor: string; action: string }): void {
  try {
    localStorage.setItem(LAST_AUDIT_VIEW_KEY, JSON.stringify(view))
  } catch {
    // Private browsing or full storage — the view is simply not restored.
  }
}

function isoDaysAgo(days: number): string {
  return new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString()
}

function formatTimestamp(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString()
}

/**
 * The audit trail's historical view (design §11). Live streaming and the
 * before/after diff modal are deliberately not here yet — this page reads the
 * same bounded windows the API requires (design §9) and pages by cursor.
 */
export function AuditTrailList() {
  const persisted = loadLastView()
  const [trail, setTrailState] = useState<TrailKey>(() =>
    persisted.trail === "access" || persisted.trail === "all" ? persisted.trail : "changes",
  )
  const [from, setFrom] = useState(() => isoDaysAgo(7))
  const [to, setTo] = useState(() => new Date().toISOString())
  const [actor, setActorState] = useState(() => persisted.actor ?? "")
  const [action, setActionState] = useState(() => persisted.action ?? "")

  const [items, setItems] = useState<AuditEntry[]>([])
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  // Starts true: the first load is kicked off by the effect below, and every
  // state write in `load` happens after an await (the repo's lint forbids
  // synchronous setState inside an effect body).
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const requestId = useRef(0)

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

  // Trail/actor/action setters persist through the same key the view
  // restores from on mount (same localStorage habit as memories/scope.ts).
  const setTrail = (t: TrailKey) => {
    setTrailState(t)
    setLoading(true)
    saveLastView({ trail: t, actor, action })
  }
  const setActor = (v: string) => {
    setActorState(v)
    saveLastView({ trail, actor: v, action })
  }
  const setAction = (v: string) => {
    setActionState(v)
    saveLastView({ trail, actor, action: v })
  }

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

  return (
    <div className="flex flex-1 flex-col gap-4 p-4 pt-4">
      <div className="flex flex-col gap-1">
        <h1 className="text-2xl font-semibold">Audit Trail</h1>
        <p className="text-sm text-muted-foreground">
          Every recorded Eling operation — state changes and reads — for the
          selected window. The API only answers bounded windows.
        </p>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <div className="flex items-center gap-1 rounded-lg border p-1">
          {TRAILS.map((t) => (
            <Button
              key={t.key}
              size="sm"
              variant={trail === t.key ? "default" : "ghost"}
              onClick={() => {
                setLoading(true)
                setTrail(t.key)
              }}
            >
              {t.label}
            </Button>
          ))}
        </div>
        <Input
          aria-label="From (ISO-8601 UTC)"
          className="w-64"
          value={from}
          onChange={(e) => setFrom(e.target.value)}
          placeholder="from (ISO-8601 UTC)"
        />
        <Input
          aria-label="To (ISO-8601 UTC)"
          className="w-64"
          value={to}
          onChange={(e) => setTo(e.target.value)}
          placeholder="to (ISO-8601 UTC)"
        />
        <Input
          aria-label="Actor filter"
          className="w-40"
          value={actor}
          onChange={(e) => setActor(e.target.value)}
          placeholder="actor"
        />
        <Input
          aria-label="Action filter"
          className="w-44"
          value={action}
          onChange={(e) => setAction(e.target.value)}
          placeholder="action (e.g. memory_save)"
        />
        <Button size="sm" variant="outline" onClick={() => { setLoading(true); void load(null) }} disabled={loading}>
          <RefreshCw className={loading ? "size-4 animate-spin" : "size-4"} />
          Refresh
        </Button>
      </div>

      {error && (
        <div className="rounded-lg border border-destructive/40 p-3 text-sm text-destructive">
          {error}
        </div>
      )}

      <div className="rounded-xl border">
        <table className="w-full text-sm">
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
                <td className="p-3 whitespace-nowrap">{formatTimestamp(e.timestamp)}</td>
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
                <td className="p-3 max-w-[240px] truncate font-mono text-xs">
                  {e.target ?? e.path ?? "—"}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {nextCursor && (
        <div className="flex justify-center">
          <Button variant="outline" onClick={() => { setLoading(true); void load(nextCursor) }} disabled={loading}>
            {loading ? "Loading…" : "Load more"}
          </Button>
        </div>
      )}
    </div>
  )
}
