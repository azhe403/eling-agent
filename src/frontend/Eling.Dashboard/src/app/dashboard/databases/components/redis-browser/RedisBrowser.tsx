import { Card, CardHeader, CardTitle, CardDescription, CardContent } from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Switch } from "@/components/ui/switch"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { X } from "lucide-react"
import { useEffect, useMemo, useState } from "react"
import { DbConn, DbTab, RedisPage, RedisInspect } from "@/app/dashboard/databases/types"
import { RedisPrefixTree } from "@/app/dashboard/databases/redis-tree"
import type { RedisPrefixPreview, RedisTtlMode } from "@/app/dashboard/databases/redis-browser"
import { RedisDeleteConfirmDialog } from "../dialogs/RedisDeleteConfirmDialog"
import { RedisPrefixDeleteConfirmDialog } from "../dialogs/RedisPrefixDeleteConfirmDialog"
import { pageAsOf, inspectAsOf, redisPageKey, redisPageScope, redisTtlLabel } from "@/app/dashboard/databases/redis-browser"

export interface RedisBrowserProps {
  activeTable: DbTab | null
  activeRedisDb: number
  redisPrefix: string
  redisRootPrefix: string
  redisPages: Record<string, RedisPage>
  redisExpanded: Set<string>
  redisInspectKey: string
  redisLoading: boolean
  redisInspect: RedisInspect | null
  redisInspectOpen: boolean
  redisAutoRefresh: boolean
  redisAutoRefreshMs: number
  conns: DbConn[]
  isWide: boolean
  onPrefixChange: (prefix: string) => void
  onBrowsePrefix: (connId: string, db: number, prefix: string) => void
  onRootPrefix: (connId: string, db: number) => void
  onTogglePrefix: (connId: string, prefix: string) => void
  onSelectKey: (key: string) => void
  onLoadMore: (connId: string, db: number, prefix: string) => void
  onCollapseAll: (connId: string, db: number) => void
  onCloseInspect: () => void
  onInspectOpenChange: (open: boolean) => void
  redisDeleteTarget: string | null
  redisDeleteOpen: boolean
  redisPreview: RedisPrefixPreview | null
  redisPreviewLoading: boolean
  redisMutating: boolean
  redisPrefixPreviewOpen: boolean
  redisPrefixPreviewTarget: string | null
  onDeleteTargetChange: (key: string | null) => void
  onDeleteOpenChange: (open: boolean) => void
  onPrefixPreviewOpenChange: (open: boolean) => void
  onPreviewPrefix: (connId: string, db: number, prefix: string) => void
  onConfirmDeleteKey: (connId: string, db: number, key: string) => void
  onConfirmDeletePrefix: (connId: string, db: number, prefix: string) => void
  onAutoRefreshChange: (enabled: boolean) => void
  onAutoRefreshMsChange: (ms: number) => void
}

export const RedisBrowser = ({
  activeTable,
  activeRedisDb,
  redisPrefix,
  redisRootPrefix,
  redisPages,
  redisExpanded,
  redisInspectKey,
  redisLoading,
  redisInspect,
  redisInspectOpen,
  conns,
  isWide,
  onPrefixChange,
  onBrowsePrefix,
  onRootPrefix,
  onTogglePrefix,
  onSelectKey,
  onLoadMore,
  onCollapseAll,
  onCloseInspect,
  onInspectOpenChange,
  redisDeleteTarget,
  redisDeleteOpen,
  redisPreview,
  redisPreviewLoading,
  redisMutating,
  redisPrefixPreviewOpen,
  redisPrefixPreviewTarget,
  onDeleteTargetChange,
  onDeleteOpenChange,
  onPrefixPreviewOpenChange,
  onPreviewPrefix,
  onConfirmDeleteKey,
  onConfirmDeletePrefix,
  redisAutoRefresh,
  redisAutoRefreshMs,
  onAutoRefreshChange,
  onAutoRefreshMsChange,
}: RedisBrowserProps) => {
  const [redisTtlMode, setRedisTtlMode] = useState<RedisTtlMode>(() => {
    if (typeof window === "undefined") return "countdown"
    const saved = window.localStorage.getItem("redis.ttlMode")
    return saved === "datetime" ? "datetime" : "countdown"
  })
  const [redisTtlNow, setRedisTtlNow] = useState<number>(() => Date.now())

  useEffect(() => {
    try {
      window.localStorage.setItem("redis.ttlMode", redisTtlMode)
    } catch {
      // storage unavailable: the mode still applies for this session
    }
  }, [redisTtlMode])

  useEffect(() => {
    const timer = setInterval(() => setRedisTtlNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [])

  const connId = activeTable?.connId ?? ""
  const rootKey = redisPageKey(connId, activeRedisDb, redisRootPrefix)
  const activePage = redisPages[rootKey]
  const ttlNow = redisTtlNow

  /**
   * Whether anything on screen can count down. A tree of keys without an
   * expiry has nothing to tick, so the clock stays off and the rows do not
   * re-render once a second for nothing.
   */
  const anyKeyHasExpiry = useMemo(
    () =>
      Object.values(redisPages).some((page) =>
        page.keys.some((key) => Boolean(key.expiresAt) || key.ttlSeconds > 0)
      ),
    [redisPages]
  )

  /**
   * The counts of the pages in view. A root prefix hides the rest of the
   * database, so a count of what is off-screen would read as if the filter
   * did nothing.
   */
  const counts = useMemo(() => {
    const scope = redisPageScope(connId, activeRedisDb)
    const dbPages = Object.entries(redisPages)
      .filter(([key]) => key.startsWith(scope))
      .map(([, page]) => page)
      .filter((page) => redisRootPrefix === "" || page.prefix.startsWith(redisRootPrefix))
    if (dbPages.length === 0) return null
    const prefixes = dbPages.reduce((sum, p) => sum + p.nodes.length, 0)
    const keys = dbPages.reduce((sum, p) => sum + p.keys.length, 0)
    const scanned = activePage ? ` · scanned ${activePage.scanned} so far` : ""
    const continued = activePage && !activePage.complete ? " (more)" : ""
    const cached = pageAsOf(activePage)
    const label =
      redisRootPrefix === "" ? `${prefixes} prefixes · ${keys} keys (this level)` : `${prefixes} prefixes · ${keys} keys under ${redisRootPrefix}`
    return `${label}${continued}${scanned}${cached ? ` · ${cached}` : ""}`
  }, [redisPages, connId, activeRedisDb, redisRootPrefix, activePage])

  /**
   * The freshest TTL for the inspected key lives in the page, not in the
   * click-time inspect payload: the payload is a snapshot of the moment the
   * reader opened it, and it has no way to know the key was rewritten with a
   * fresh expiry. If the key is on screen, the counts line below is what the
   * countdown reads; the payload keeps the type, length and preview.
   *
   * The fallback is the payload itself, so a key that is currently scrolled
   * out of every page still shows a stale-but-honest TTL instead of nothing.
   */
  const inspectedLeaf = useMemo(() => {
    if (!redisInspect) return null
    for (const page of Object.values(redisPages)) {
      const found = page.keys.find((key) => key.key === redisInspect.key)
      if (found) return found
    }
    return null
  }, [redisPages, redisInspect])

  /** The preview of the inspected key. Shared between the docked column and
   * the popup used on narrower screens, so both show exactly the same preview.
   *
   * Stacked field/value rows instead of a table: the docked column is 360px
   * and a hash field can be a long JSON blob, so table columns forced a
   * horizontal scrollbar and clipped the value mid-word.
   */
  const inspectBody = redisInspect ? (
    <>
      {redisInspect.preview.entries.length > 0 ? (
        <div className="space-y-1">
          {redisInspect.preview.entries.map((entry, i) => (
            <div key={`${entry.field ?? "item"}-${i}`} className="rounded border px-2 py-1">
              {entry.field !== null ? (
                <p className="break-all font-mono text-[11px] text-muted-foreground">{entry.field}</p>
              ) : null}
              <p className="whitespace-pre-wrap break-all text-xs">{entry.value ?? "null"}</p>
            </div>
          ))}
        </div>
      ) : (
        <p className="text-sm text-muted-foreground">No preview entries.</p>
      )}
      {redisInspect.preview.truncated ? <p className="text-xs text-amber-600">Preview truncated.</p> : null}
    </>
  ) : null

  // The TTL shown in the inspector's title is read from the page (which the
  // server keeps current) when the key is on screen, and from the click-time
  // payload otherwise — so an open detail re-counts down as soon as the key is
  // written back with a fresh expiry.
  const inspectMeta = redisInspect
    ? `${redisInspect.type} · len ${redisInspect.length} · ${redisTtlLabel(inspectedLeaf ?? redisInspect, ttlNow, redisTtlMode)}`
    : ""
  const inspectSnapshot = inspectAsOf(redisInspect)

  return (
    <Card className="flex-1 min-h-0">
      <CardHeader>
        <CardTitle>
          Redis · {conns.find((c) => c.id === activeTable?.connId)?.name ?? ""} · db{activeRedisDb}
        </CardTitle>
        <CardDescription>
          Browse keys for logical database {activeRedisDb}. Click a key to inspect its value.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-1 flex-col min-h-0 gap-3">
        <div className="flex shrink-0 flex-wrap items-center gap-2">
          <Input
            className="flex-1"
            placeholder="prefix — Enter to browse under it"
            value={redisPrefix}
            onChange={(e) => onPrefixChange(e.target.value)}
            onKeyDown={(e) => {
              if (e.key !== "Enter") return
              if (!connId) return
              onBrowsePrefix(connId, activeRedisDb, redisPrefix)
            }}
            disabled={!connId}
          />
          <Button
            variant="outline"
            onClick={() => {
              if (!connId) return
              onBrowsePrefix(connId, activeRedisDb, redisPrefix)
            }}
            disabled={redisLoading || !connId}
          >
            Browse
          </Button>
          <Button
            variant="ghost"
            onClick={() => {
              if (!connId) return
              onRootPrefix(connId, activeRedisDb)
            }}
            disabled={redisLoading || !connId}
          >
            {redisLoading ? "Browsing…" : "Root"}
          </Button>
          <div className="flex w-[200px] shrink-0 items-center gap-2 rounded-md border px-2 py-1 dark:border-input" title="AutoRefresh: background revalidate of the root page">
            <Switch
              size="sm"
              checked={redisAutoRefresh}
              onCheckedChange={(checked) => onAutoRefreshChange(Boolean(checked))}
              aria-label="AutoRefresh reload keys"
              disabled={!connId}
            />
            <span className="text-xs font-medium">AutoRefresh</span>
            <Select value={`${redisAutoRefreshMs / 1000}s`} onValueChange={(v) => { if (v) onAutoRefreshMsChange(Number(v.replace("s", "")) * 1000) }} disabled={!redisAutoRefresh || !connId}>
              <SelectTrigger size="sm" className="ml-auto w-[96px]" aria-label="AutoRefresh interval">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="10s">10s</SelectItem>
                <SelectItem value="15s">15s</SelectItem>
                <SelectItem value="30s">30s</SelectItem>
                <SelectItem value="60s">60s</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div className="flex shrink-0 items-center gap-2 rounded-md border px-2 py-1 dark:border-input" title="How a key's TTL is written: a live countdown, or the moment it expires">
            <span className="text-xs font-medium">TTL</span>
            <Select value={redisTtlMode} onValueChange={(v) => { if (v === "countdown" || v === "datetime") setRedisTtlMode(v) }} disabled={!connId}>
              <SelectTrigger size="sm" className="w-[104px]" aria-label="TTL display mode">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="countdown">H:M:S</SelectItem>
                <SelectItem value="datetime">Date</SelectItem>
              </SelectContent>
            </Select>
          </div>
        </div>

        <div className={`grid min-h-0 flex-1 grid-rows-[1fr] gap-2 ${isWide && redisInspect ? "xl:grid-cols-[360px_minmax(0,1fr)]" : ""}`}>
          <div className="flex min-h-0 min-w-0 flex-col overflow-hidden rounded-md border p-3">
            <div className="flex shrink-0 items-center justify-between gap-2">
              <p className="text-xs text-muted-foreground">{counts ?? "No Redis key tree loaded yet."}</p>
              <div className="flex shrink-0 items-center gap-2">
                {activePage && !activePage.complete ? (
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => connId && onLoadMore(connId, activeRedisDb, redisRootPrefix)}
                    disabled={redisLoading || !connId}
                  >
                    {redisLoading ? "Loading…" : "Load more"}
                  </Button>
                ) : null}
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => onCollapseAll(connId, activeRedisDb)}
                  disabled={!connId}
                >
                  Collapse all
                </Button>
              </div>
            </div>
            <div className="min-h-0 flex-1 overflow-y-auto pr-1" style={{ scrollbarGutter: "stable" }}>
              {redisPages[rootKey] ? (
                <RedisPrefixTree
                  connId={connId}
                  db={activeRedisDb}
                  prefix={redisRootPrefix}
                  pages={redisPages}
                  expanded={redisExpanded}
                  inspectKey={redisInspectKey}
                  onToggle={(prefix) => connId && onTogglePrefix(connId, prefix)}
                  onSelectKey={(key) => onSelectKey(key)}
                  onDeleteKey={(key) => { onDeleteTargetChange(key); onDeleteOpenChange(true) }}
                  onDeletePrefix={(prefix) => { if (connId) void onPreviewPrefix(connId, activeRedisDb, prefix) }}
                  ttlMode={redisTtlMode}
                  now={ttlNow}
                />
              ) : (
                <p className="text-sm text-muted-foreground">
                  {redisRootPrefix === "" ? "Press Root to load the key tree." : `Nothing loaded under ${redisRootPrefix} — press Browse.`}
                </p>
              )}
            </div>
          </div>

          {isWide && redisInspect ? (
            <div className="flex min-h-0 flex-col rounded-md border p-3">
              <div className="flex shrink-0 items-start justify-between gap-2">
                <p className="min-w-0 break-all font-mono text-xs font-medium">{redisInspect.key}</p>
                <Button
                  variant="ghost"
                  size="sm"
                  className="size-6 shrink-0 p-0"
                  aria-label="Close key inspector"
                  onClick={onCloseInspect}
                >
                  <X className="size-3.5" />
                </Button>
              </div>
              <p className="shrink-0 text-xs text-muted-foreground">{inspectMeta}</p>
              {inspectSnapshot ? <p className="shrink-0 text-xs text-amber-600">{inspectSnapshot} · asking the server in the background</p> : null}
              <div className="min-h-0 flex-1 overflow-y-auto">{inspectBody}</div>
            </div>
          ) : null}
        </div>

        {!isWide && redisInspectOpen && redisInspect !== null ? (
          <Dialog open onOpenChange={(o) => { if (!o) onInspectOpenChange(false) }}>
            <DialogContent className="max-h-[85dvh] overflow-y-auto sm:max-w-2xl">
              <DialogHeader>
                <DialogTitle className="break-all font-mono text-sm">{redisInspect.key}</DialogTitle>
              </DialogHeader>
              <p className="text-xs text-muted-foreground">{inspectMeta}</p>
              {inspectSnapshot ? <p className="text-xs text-amber-600">{inspectSnapshot} · asking the server in the background</p> : null}
              {inspectBody}
            </DialogContent>
          </Dialog>
        ) : null}

        {connId && redisDeleteTarget ? (
          <RedisDeleteConfirmDialog
            open={redisDeleteOpen}
            keyName={redisDeleteTarget}
            mutating={redisMutating}
            onConfirm={() => void onConfirmDeleteKey(connId, activeRedisDb, redisDeleteTarget)}
            onCancel={() => { onDeleteTargetChange(null); onDeleteOpenChange(false) }}
          />
        ) : null}

        {connId && redisPrefixPreviewTarget ? (
          <RedisPrefixDeleteConfirmDialog
            open={redisPrefixPreviewOpen}
            prefix={redisPreview?.prefix ?? redisPrefixPreviewTarget}
            total={redisPreview?.total ?? 0}
            keys={redisPreview?.keys ?? []}
            truncated={redisPreview?.truncated ?? false}
            loading={redisPreviewLoading}
            mutating={redisMutating}
            onConfirm={() => void onConfirmDeletePrefix(connId, activeRedisDb, redisPrefixPreviewTarget)}
            onCancel={() => onPrefixPreviewOpenChange(false)}
          />
        ) : null}
      </CardContent>
    </Card>
  )
}
